using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Win2Linux.Core.Distros;
using Win2Linux.Core.Orchestrator;

namespace Win2Linux.Core.Download;

/// <summary>
/// Handles downloading a Linux distribution ISO and verifying its SHA256 checksum.
/// Downloads are streamed directly to disk and support resuming via HTTP Range requests.
/// </summary>
public static class IsoDownloadService
{
    private static readonly HttpClient _http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        })
        {
            Timeout = TimeSpan.FromHours(4)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Win2Linux/1.0");
        return client;
    }

    /// <summary>
    /// Locates the repository 'repo/iso' directory by searching upward from the application base
    /// directory or current working directory for markers like .git, Win2Linux.sln, or repo/iso.
    /// Creates the directory if it does not exist.
    /// </summary>
    public static string GetRepoIsoDirectory()
    {
        var startDirs = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var startDir in startDirs)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                var candidateRepoIso = Path.Combine(dir.FullName, "repo", "iso");
                if (Directory.Exists(candidateRepoIso))
                    return candidateRepoIso;

                if (File.Exists(Path.Combine(dir.FullName, "Win2Linux.sln")) ||
                    Directory.Exists(Path.Combine(dir.FullName, ".git")))
                {
                    Directory.CreateDirectory(candidateRepoIso);
                    return candidateRepoIso;
                }

                dir = dir.Parent;
            }
        }

        var fallback = Path.Combine(Directory.GetCurrentDirectory(), "repo", "iso");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>
    /// Returns the default target path for downloading an ISO into the repo/iso directory.
    /// </summary>
    public static string GetDefaultIsoDownloadPath(DistroProfile distro)
    {
        return Path.Combine(GetRepoIsoDirectory(), $"{distro.Id}-{distro.DefaultVersion}.iso");
    }

    /// <summary>
    /// Infers which DistroProfile corresponds to a given ISO filename or path.
    /// </summary>
    public static DistroProfile? InferDistroFromIso(string isoPathOrFileName)
    {
        var name = Path.GetFileName(isoPathOrFileName).ToLowerInvariant();

        if (name.Contains("ubuntu"))
            return DistroRegistry.SupportedDistributions.FirstOrDefault(d => d.Id == "ubuntu");

        if (name.Contains("fedora"))
            return DistroRegistry.SupportedDistributions.FirstOrDefault(d => d.Id == "fedora");

        if (name.Contains("mint"))
            return DistroRegistry.SupportedDistributions.FirstOrDefault(d => d.Id == "linuxmint");

        if (name.Contains("zorin"))
            return DistroRegistry.SupportedDistributions.FirstOrDefault(d => d.Id == "zorin");

        return null;
    }

    /// <summary>
    /// Searches repo/iso (or searchDir) to infer if an existing ISO matches the specified DistroProfile.
    /// </summary>
    public static string? InferIsoPath(DistroProfile distro, string? searchDir = null)
    {
        var targetDir = searchDir ?? GetRepoIsoDirectory();
        if (!Directory.Exists(targetDir)) return null;

        var expectedName = $"{distro.Id}-{distro.DefaultVersion}.iso";
        var exactPath = Path.Combine(targetDir, expectedName);
        if (File.Exists(exactPath) && new FileInfo(exactPath).Length > 0)
            return exactPath;

        try
        {
            var uriName = Path.GetFileName(new Uri(distro.IsoDownloadUrl).AbsolutePath);
            var uriPath = Path.Combine(targetDir, uriName);
            if (File.Exists(uriPath) && new FileInfo(uriPath).Length > 0)
                return uriPath;
        }
        catch { }

        // Search directory for candidate .iso files containing distro ID
        var isoFiles = Directory.GetFiles(targetDir, "*.iso", SearchOption.TopDirectoryOnly);
        foreach (var file in isoFiles)
        {
            var fileName = Path.GetFileName(file).ToLowerInvariant();
            if (distro.Id == "fedora")
            {
                if (fileName.Contains("fedora") && !fileName.Contains("netinst") && !fileName.Contains("everything"))
                    return file;
            }
            else if (distro.Id == "linuxmint" && (fileName.Contains("linuxmint") || fileName.Contains("mint")))
            {
                return file;
            }
            else if (distro.Id == "zorin" && fileName.Contains("zorin"))
            {
                return file;
            }
            else if (fileName.Contains(distro.Id.ToLowerInvariant()))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>
    /// Scans the repo/iso directory for available ISO files and infers their corresponding distro profile.
    /// </summary>
    public static IReadOnlyList<(DistroProfile? Distro, string IsoPath, long SizeBytes)> InferAvailableIsos(string? repoIsoDir = null)
    {
        var targetDir = repoIsoDir ?? GetRepoIsoDirectory();
        if (!Directory.Exists(targetDir))
            return Array.Empty<(DistroProfile?, string, long)>();

        var results = new List<(DistroProfile?, string, long)>();
        foreach (var file in Directory.GetFiles(targetDir, "*.iso", SearchOption.TopDirectoryOnly))
        {
            var fi = new FileInfo(file);
            var distro = InferDistroFromIso(file);
            results.Add((distro, file, fi.Length));
        }

        return results;
    }

    /// <summary>
    /// Downloads the ISO for the given distro to the specified staging directory (or repo/iso by default).
    /// If an ISO can be inferred from repo/iso or cache and its hash matches, the download is skipped.
    /// </summary>
    /// <returns>Absolute path to the verified ISO file.</returns>
    public static async Task<string> DownloadIsoAsync(
        DistroProfile distro,
        string? isoStagingDir,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct = default)
    {
        var targetDir = string.IsNullOrWhiteSpace(isoStagingDir) ? GetRepoIsoDirectory() : isoStagingDir;
        Directory.CreateDirectory(targetDir);

        // Check if matching ISO can be inferred from repo/iso or target directory
        var inferredPath = InferIsoPath(distro, targetDir) ?? InferIsoPath(distro, GetRepoIsoDirectory());
        var isoFileName = $"{distro.Id}-{distro.DefaultVersion}.iso";
        var isoPath = inferredPath ?? Path.Combine(targetDir, isoFileName);

        if (inferredPath != null)
        {
            progress.Report(new ProgressUpdate(3, 10, "ISO Inferred from repo/iso", 22,
                $"[ISO Inference] Detected pre-existing ISO in repo/iso: {Path.GetFileName(inferredPath)}"));
        }

        // --- Step 1: Fetch expected SHA256 checksum ---
        // NOTE: We do NOT save the sidecar here. The sidecar is only written by VerifyIsoAsync
        // after a successful hash verification, preventing stale pre-fetch sidecars from
        // masking download failures on subsequent runs.
        progress.Report(new ProgressUpdate(3, 10, "Downloading ISO", 22,
            $"[ISO Download] Fetching SHA256 checksum for {distro.DisplayName}..."));

        string expectedHash = await FetchExpectedHashAsync(distro, isoPath, saveSidecar: false, ct);

        // --- Step 2: Check local cache / inferred ISO ---
        if (File.Exists(isoPath) && new FileInfo(isoPath).Length > 0)
        {
            progress.Report(new ProgressUpdate(3, 10, "Downloading ISO", 23,
                $"[ISO Download] Verifying integrity of {(inferredPath != null ? "inferred" : "cached")} ISO ({new FileInfo(isoPath).Length / (1024.0 * 1024.0):F0} MB)..."));

            string existingHash = await ComputeFileHashAsync(isoPath, ct);
            if (string.Equals(existingHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                progress.Report(new ProgressUpdate(3, 10, "ISO Verified from Cache", 30,
                    $"[ISO Download] Cache hit — ISO hash verified ({expectedHash[..16]}...). Skipping download."));
                return isoPath;
            }

            progress.Report(new ProgressUpdate(3, 10, "Downloading ISO", 23,
                $"[ISO Download] Cached ISO hash mismatch. Re-downloading to {isoPath}..."));
        }

        // Delete any existing partial/empty file before starting fresh
        if (File.Exists(isoPath))
        {
            try { File.Delete(isoPath); } catch { }
        }

        // --- Step 3: Build ordered mirror list ---
        // IsoMirrorUrls (if set) are tried first in order; IsoDownloadUrl is the canonical fallback.
        // This allows fast regional mirrors to be preferred while still keeping the official URL as last resort.
        var mirrorList = (distro.IsoMirrorUrls != null && distro.IsoMirrorUrls.Length > 0)
            ? distro.IsoMirrorUrls
            : new[] { distro.IsoDownloadUrl };

        progress.Report(new ProgressUpdate(3, 10, "Downloading ISO", 23,
            $"[ISO Download] {mirrorList.Length} mirror(s) available. Starting with fastest mirror..." +
            $"\n[ISO Download] Manual fallback: place the ISO at {isoPath} to skip download entirely."));

        // --- Step 4: Try each mirror in order ---
        const long MinIsoBytes = 50L * 1024 * 1024; // 50 MB minimum for a real ISO
        var mirrorErrors = new List<string>();
        long totalBytes = 0;
        long downloadedBytes = 0;

        for (int mirrorIndex = 0; mirrorIndex < mirrorList.Length; mirrorIndex++)
        {
            var mirrorUrl = mirrorList[mirrorIndex]!;
            var mirrorLabel = mirrorIndex == mirrorList.Length - 1 ? "official CDN" : $"mirror {mirrorIndex + 1}";

            progress.Report(new ProgressUpdate(3, 10, "Downloading ISO", 23,
                $"[ISO Download] Trying {mirrorLabel}: {mirrorUrl}"));

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, mirrorUrl);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                var statusCode = (int)response.StatusCode;
                var contentLength = response.Content.Headers.ContentLength;
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";

                progress.Report(new ProgressUpdate(3, 10, "Downloading ISO", 23,
                    $"[ISO Download] HTTP {statusCode} — Content-Type: {contentType} — " +
                    $"Content-Length: {(contentLength.HasValue ? $"{contentLength.Value / (1024.0 * 1024.0):F0} MB" : "unknown")}"));

                if (!response.IsSuccessStatusCode)
                {
                    mirrorErrors.Add($"{mirrorLabel}: HTTP {statusCode} {response.ReasonPhrase}");
                    progress.Report(new ProgressUpdate(3, 10, "Downloading ISO", 23,
                        $"[ISO Download] {mirrorLabel} returned HTTP {statusCode} — trying next mirror..."));
                    continue;
                }

                // Reject HTML responses (bot-detection, captcha, geo-block from CDN)
                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase))
                {
                    string? snippet = null;
                    try
                    {
                        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                        snippet = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 200)).Trim();
                    }
                    catch { }
                    mirrorErrors.Add($"{mirrorLabel}: returned HTML (bot-detection or geo-block). Preview: {snippet}");
                    progress.Report(new ProgressUpdate(3, 10, "Downloading ISO", 23,
                        $"[ISO Download] {mirrorLabel} returned HTML page instead of ISO — likely geo-throttled. Trying next mirror..."));
                    continue;
                }

                // Reject explicit Content-Length: 0
                if (contentLength.HasValue && contentLength.Value == 0)
                {
                    mirrorErrors.Add($"{mirrorLabel}: Content-Length was 0");
                    progress.Report(new ProgressUpdate(3, 10, "Downloading ISO", 23,
                        $"[ISO Download] {mirrorLabel} returned Content-Length: 0 — trying next mirror..."));
                    continue;
                }

                // Stream download to disk
                totalBytes = contentLength ?? 0;
                downloadedBytes = 0;
                double lastReportedPercent = -1;

                using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = new FileStream(isoPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    downloadedBytes += bytesRead;

                    if (totalBytes > 0)
                    {
                        double downloadPercent = (double)downloadedBytes / totalBytes;
                        double overallPercent = 22 + downloadPercent * 8;
                        double roundedPercent = Math.Round(overallPercent, 1);
                        if (roundedPercent != lastReportedPercent)
                        {
                            lastReportedPercent = roundedPercent;
                            var mbDown = downloadedBytes / (1024.0 * 1024.0);
                            var mbTotal = totalBytes / (1024.0 * 1024.0);
                            progress.Report(new ProgressUpdate(3, 10, "Downloading ISO", roundedPercent,
                                $"[ISO Download] [{mirrorLabel}] {mbDown:F0} MB / {mbTotal:F0} MB  ({downloadPercent:P0})"));
                        }
                    }
                }

                await fileStream.FlushAsync(ct);
                long finalSize = new FileInfo(isoPath).Length;

                // Reject 0-byte or suspiciously small files
                if (finalSize == 0)
                {
                    mirrorErrors.Add($"{mirrorLabel}: downloaded 0 bytes (empty response body)");
                    progress.Report(new ProgressUpdate(3, 10, "Downloading ISO", 23,
                        $"[ISO Download] {mirrorLabel} sent 0 bytes — trying next mirror..."));
                    try { File.Delete(isoPath); } catch { }
                    continue;
                }

                if (finalSize < MinIsoBytes)
                {
                    mirrorErrors.Add($"{mirrorLabel}: downloaded only {finalSize / 1024.0:F1} KB (< 50 MB minimum for a real ISO)");
                    progress.Report(new ProgressUpdate(3, 10, "Downloading ISO", 23,
                        $"[ISO Download] {mirrorLabel} returned only {finalSize / 1024.0:F0} KB — likely an error page. Trying next mirror..."));
                    try { File.Delete(isoPath); } catch { }
                    continue;
                }

                // Reject incomplete download when server gave a Content-Length
                if (totalBytes > 0 && finalSize < totalBytes)
                {
                    var pct = finalSize * 100.0 / totalBytes;
                    mirrorErrors.Add($"{mirrorLabel}: incomplete — got {finalSize / (1024.0 * 1024.0):F0} MB of {totalBytes / (1024.0 * 1024.0):F0} MB ({pct:F1}%)");
                    progress.Report(new ProgressUpdate(3, 10, "Downloading ISO", 23,
                        $"[ISO Download] {mirrorLabel} transfer was cut short at {pct:F0}% — trying next mirror..."));
                    try { File.Delete(isoPath); } catch { }
                    continue;
                }

                // Success
                progress.Report(new ProgressUpdate(3, 10, "Download Complete — Verifying", 30,
                    $"[ISO Download] ✓ Downloaded {finalSize / (1024.0 * 1024.0):F0} MB from {mirrorLabel}. Verifying SHA256..."));
                return isoPath;
            }
            catch (IsoDownloadException) { throw; }  // Re-throw our own typed exceptions
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                mirrorErrors.Add($"{mirrorLabel}: {ex.GetType().Name} — {ex.Message}");
                progress.Report(new ProgressUpdate(3, 10, "Downloading ISO", 23,
                    $"[ISO Download] {mirrorLabel} error: {ex.Message} — trying next mirror..."));
                try { if (File.Exists(isoPath)) File.Delete(isoPath); } catch { }
            }
        }

        // All mirrors exhausted
        var errorSummary = string.Join("\n  • ", mirrorErrors);
        throw new IsoDownloadException(
            $"All {mirrorList.Length} mirror(s) failed for {distro.DisplayName} ISO.\n" +
            $"  • {errorSummary}\n\n" +
            $"Manual fix: download the ISO from any mirror and place it at:\n" +
            $"  {isoPath}\n" +
            $"The app will detect it automatically on the next run.");

    }

    /// <summary>
    /// Verifies the SHA256 hash of the downloaded ISO against the expected value.
    /// On success, writes the sidecar cache file so future runs can skip re-fetching from network.
    /// </summary>
    public static async Task<string> VerifyIsoAsync(
        DistroProfile distro,
        string isoPath,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct = default)
    {
        var fileSizeMb = new FileInfo(isoPath).Length / (1024.0 * 1024.0);
        progress.Report(new ProgressUpdate(4, 10, "Verifying ISO Checksum", 31,
            $"[Checksum] Computing SHA256 of {Path.GetFileName(isoPath)} ({fileSizeMb:F0} MB)..."));

        string actualHash = await ComputeFileHashAsync(isoPath, ct);

        progress.Report(new ProgressUpdate(4, 10, "Verifying ISO Checksum", 33,
            $"[Checksum] SHA256 = {actualHash[..32]}..."));

        // Fetch expected hash fresh from network (no sidecar read here — sidecar not trusted until we verify)
        string expectedHash = await FetchExpectedHashAsync(distro, isoPath, saveSidecar: false, ct);

        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(isoPath); // Remove the corrupted/tampered file
            throw new ChecksumMismatchException(
                $"SHA256 mismatch for {distro.DisplayName} ISO!\n" +
                $"  Expected: {expectedHash}\n" +
                $"  Actual:   {actualHash}\n" +
                $"The downloaded file has been deleted. Please try again.");
        }

        // Hash verified — now it is safe to persist the sidecar for future cache hits
        try
        {
            var sidecar = Path.ChangeExtension(isoPath, ".sha256");
            await File.WriteAllTextAsync(sidecar, actualHash, ct);
        }
        catch { }

        progress.Report(new ProgressUpdate(4, 10, "ISO Checksum Verified", 35,
            $"[Checksum] ✓ SHA256 verified ({actualHash[..16]}...). ISO is authentic."));

        return actualHash;
    }

    public static string? ParseExpectedHash(string checksumContent, string isoFileName)
    {
        if (string.IsNullOrWhiteSpace(checksumContent))
            return null;

        // 1. Try matching lines containing the full ISO filename (handles Coreutils and BSD/OpenSSL formats)
        // e.g. "HASH  filename" or "SHA256 (filename) = HASH"
        foreach (var line in checksumContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains(isoFileName, StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(trimmed, @"\b([0-9a-fA-F]{64})\b");
                if (match.Success)
                {
                    return match.Groups[1].Value.ToLowerInvariant();
                }
            }
        }

        // 2. Fallback: match by filename without extension
        var baseName = Path.GetFileNameWithoutExtension(isoFileName);
        foreach (var line in checksumContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains(baseName, StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(trimmed, @"\b([0-9a-fA-F]{64})\b");
                if (match.Success)
                {
                    return match.Groups[1].Value.ToLowerInvariant();
                }
            }
        }

        // 3. Fallback for Fedora variant naming (e.g. Fedora-KDE-Desktop-Live vs Fedora-KDE)
        if (isoFileName.Contains("fedora", StringComparison.OrdinalIgnoreCase))
        {
            var isKde = isoFileName.Contains("kde", StringComparison.OrdinalIgnoreCase);
            var isWorkstation = isoFileName.Contains("workstation", StringComparison.OrdinalIgnoreCase);

            foreach (var line in checksumContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if ((isKde && trimmed.Contains("kde", StringComparison.OrdinalIgnoreCase)) ||
                    (isWorkstation && trimmed.Contains("workstation", StringComparison.OrdinalIgnoreCase)))
                {
                    var match = Regex.Match(trimmed, @"\b([0-9a-fA-F]{64})\b");
                    if (match.Success)
                    {
                        return match.Groups[1].Value.ToLowerInvariant();
                    }
                }
            }
        }

        // 4. If single-file checksum (e.g. *.sha256 containing just 1 SHA256 hex string)
        var allMatches = Regex.Matches(checksumContent, @"\b([0-9a-fA-F]{64})\b");
        if (allMatches.Count == 1)
        {
            return allMatches[0].Groups[1].Value.ToLowerInvariant();
        }

        return null;
    }

    private static async Task<string> FetchExpectedHashAsync(
        DistroProfile distro,
        string? isoPath,
        bool saveSidecar,
        CancellationToken ct)
    {
        // 1. Check sidecar cache — ONLY when saveSidecar mode is active (i.e. called from VerifyIsoAsync)
        //    AND the ISO actually exists and is non-empty.
        //    The sidecar is written only after a successful verified download, so a sidecar hit
        //    guarantees the ISO on disk was good when it was last verified.
        if (saveSidecar && !string.IsNullOrEmpty(isoPath))
        {
            var sidecar = Path.ChangeExtension(isoPath, ".sha256");
            bool isoExistsAndNonEmpty = File.Exists(isoPath) && new FileInfo(isoPath).Length > 0;
            if (File.Exists(sidecar) && isoExistsAndNonEmpty)
            {
                try
                {
                    var cached = (await File.ReadAllTextAsync(sidecar, ct)).Trim();
                    if (cached.Length == 64 && Regex.IsMatch(cached, @"^[0-9a-fA-F]{64}$"))
                    {
                        return cached.ToLowerInvariant();
                    }
                }
                catch { }
            }
        }

        // 2. Candidate URLs (primary + mirrors)
        var candidateUrls = new List<string> { distro.ChecksumUrl };
        if (distro.Id == "fedora")
        {
            if (distro.SelectedDesktopEnvironment == "gnome")
            {
                candidateUrls.Add("https://mirrors.tuna.tsinghua.edu.cn/fedora/releases/44/Workstation/x86_64/iso/Fedora-Workstation-44-1.7-x86_64-CHECKSUM");
                candidateUrls.Add("https://mirrors.ustc.edu.cn/fedora/releases/44/Workstation/x86_64/iso/Fedora-Workstation-44-1.7-x86_64-CHECKSUM");
                candidateUrls.Add("https://mirror.vcu.edu/pub/gnu_linux/fedora/releases/44/Workstation/x86_64/iso/Fedora-Workstation-44-1.7-x86_64-CHECKSUM");
                candidateUrls.Add("https://mirrors.rit.edu/fedora/fedora/linux/releases/44/Workstation/x86_64/iso/Fedora-Workstation-44-1.7-x86_64-CHECKSUM");
            }
            else
            {
                candidateUrls.Add("https://dl.fedoraproject.org/pub/fedora/linux/releases/44/KDE/x86_64/iso/Fedora-KDE-44-1.7-x86_64-CHECKSUM");
                candidateUrls.Add("https://mirrors.tuna.tsinghua.edu.cn/fedora/releases/44/KDE/x86_64/iso/Fedora-KDE-44-1.7-x86_64-CHECKSUM");
                candidateUrls.Add("https://mirrors.ustc.edu.cn/fedora/releases/44/KDE/x86_64/iso/Fedora-KDE-44-1.7-x86_64-CHECKSUM");
            }
        }
        else if (distro.Id == "linuxmint")
        {
            candidateUrls.Add("https://mirrors.kernel.org/linuxmint/stable/22.1/sha256sum.txt");
        }
        else if (distro.Id == "zorin")
        {
            candidateUrls.Add("https://mirrors.edge.kernel.org/zorinos-isos/17/Zorin-OS-17.2-Core-64-bit.iso.sha256");
        }

        var isoFileName = Path.GetFileName(new Uri(distro.IsoDownloadUrl).AbsolutePath);
        Exception? lastEx = null;

        foreach (var url in candidateUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var content = await resp.Content.ReadAsStringAsync(ct);
                    var hash = ParseExpectedHash(content, isoFileName);
                    if (hash != null)
                    {
                        return hash;
                    }
                }
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }

        throw new IsoDownloadException(
            $"Could not find SHA256 hash for '{isoFileName}' from {distro.ChecksumUrl} or fallback mirrors." +
            (lastEx != null ? $" Error: {lastEx.Message}" : ""));
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hashBytes = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}

public class IsoDownloadException(string message) : Exception(message);
public class ChecksumMismatchException(string message) : Exception(message);
