using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Win2Linux.Core.Models;
using Win2Linux.Core.Orchestrator;
using Win2Linux.Core.Storage;

namespace Win2Linux.Core.Networking;

/// <summary>
/// Updates the network section of an already staged Ubuntu autoinstall file without
/// rerunning disk shrink or ESP creation.
/// </summary>
public static class WifiAutoinstallUpdater
{
    private const string EspGptType = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b";
    private static readonly string StateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Win2Linux",
        "state.json");

    public static async Task UpdateExistingUbuntuAutoinstallAsync(
        WifiNetworkConfiguration wifiNetwork,
        CancellationToken cancellationToken = default)
    {
        if (wifiNetwork is null || string.IsNullOrWhiteSpace(wifiNetwork.Ssid))
        {
            throw new InvalidOperationException("Select a saved Wi-Fi network before updating the existing installer.");
        }

        if (!File.Exists(StateFilePath))
        {
            throw new InvalidOperationException("Win2Linux staging state was not found. Stage Ubuntu before using this update action.");
        }

        using var stateDocument = JsonDocument.Parse(await File.ReadAllTextAsync(StateFilePath, cancellationToken));
        var stateRoot = stateDocument.RootElement;
        var distroId = stateRoot.GetProperty("Distribution").GetProperty("Id").GetString();
        if (!string.Equals(distroId, "ubuntu", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The saved Win2Linux staging state is not for Ubuntu.");
        }

        var esp = stateRoot.GetProperty("Esp");
        var linuxTarget = stateRoot.GetProperty("LinuxTarget");
        var protectedDisk = stateRoot.GetProperty("ProtectedSystemDisk");
        var diskNumber = esp.GetProperty("DiskNumber").GetInt32();
        var partitionNumber = esp.GetProperty("PartitionNumber").GetInt32();
        var expectedGuidText = esp.GetProperty("PartitionGuid").GetString();
        var expectedGuid = Guid.TryParse(expectedGuidText, out var parsedGuid)
            ? parsedGuid
            : throw new InvalidOperationException("The saved Ubuntu ESP GUID is invalid.");

        if (linuxTarget.GetProperty("DiskNumber").GetInt32() != diskNumber ||
            protectedDisk.GetProperty("DiskNumber").GetInt32() == diskNumber)
        {
            throw new InvalidOperationException("The saved ESP is not on the verified Linux target disk.");
        }

        var discovery = await Task.Run(StorageDiscoveryService.RunDiscovery, cancellationToken);
        var matchingEsps = discovery.ExistingEsps.Where(candidate =>
            candidate.DiskNumber == diskNumber &&
            candidate.PartitionNumber == partitionNumber &&
            !candidate.IsOnSystemDisk &&
            Guid.TryParse(candidate.PartitionGuid, out var candidateGuid) && candidateGuid == expectedGuid).ToArray();
        if (matchingEsps.Length != 1)
        {
            throw new InvalidOperationException("The staged EFI partition no longer matches the saved disk and GUID. No files were changed.");
        }

        var (espRoot, addedDriveLetter) = await MountVerifiedEspAsync(
            diskNumber, partitionNumber, expectedGuid, cancellationToken);
        try
        {
            var autoinstallPath = Path.Combine(espRoot, "autoinstall.yaml");
            var stagingDirectory = Path.Combine(espRoot, "win2linux");
            if (!File.Exists(autoinstallPath) || !Directory.Exists(stagingDirectory))
            {
                throw new InvalidOperationException("The verified ESP does not contain the expected Ubuntu autoinstall files.");
            }

            var originalYaml = (await File.ReadAllTextAsync(autoinstallPath, cancellationToken)).Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!originalYaml.Contains($"/dev/disk/by-partuuid/{expectedGuid:D}", StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(originalYaml, "(?m)^        path:\\s*/dev/win2linux-target\\s*$"))
            {
                throw new InvalidOperationException("The ESP autoinstall file does not match the verified target disk. No files were changed.");
            }

            const string storageMarker = "  storage:\n";
            var storageIndex = originalYaml.IndexOf(storageMarker, StringComparison.Ordinal);
            if (storageIndex < 0)
            {
                throw new InvalidOperationException("The Ubuntu autoinstall file has no storage section. No files were changed.");
            }

            var networkIndex = originalYaml.IndexOf("  network:\n", StringComparison.Ordinal);
            if (networkIndex >= 0)
            {
                if (networkIndex > storageIndex)
                {
                    throw new InvalidOperationException("The autoinstall network section is in an unexpected location. No files were changed.");
                }

                originalYaml = originalYaml.Remove(networkIndex, storageIndex - networkIndex);
                storageIndex = networkIndex;
            }

            var networkYaml = InstallationOrchestrator.GenerateUbuntuNetworkConfig(wifiNetwork);
            var updatedYaml = originalYaml.Insert(storageIndex, networkYaml);
            InstallationOrchestrator.ValidateAutoinstallYaml(updatedYaml);

            // Keep the temporary copy under win2linux so the existing first-boot cleanup removes it
            // if Windows or the machine stops before the atomic rename completes.
            var temporaryPath = Path.Combine(stagingDirectory, "autoinstall-wifi.tmp");
            try
            {
                await File.WriteAllTextAsync(temporaryPath, updatedYaml, new UTF8Encoding(false), cancellationToken);
                File.Move(temporaryPath, autoinstallPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            if (addedDriveLetter.HasValue)
            {
                await RemoveTemporaryDriveLetterAsync(diskNumber, partitionNumber, expectedGuid, addedDriveLetter.Value);
            }
        }
    }

    private static async Task<(string RootPath, char? AddedDriveLetter)> MountVerifiedEspAsync(
        int diskNumber,
        int partitionNumber,
        Guid expectedGuid,
        CancellationToken cancellationToken)
    {
        var candidateLetter = FindFreeDriveLetter();
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $partition = Get-Partition -DiskNumber {{diskNumber}} -PartitionNumber {{partitionNumber}} -ErrorAction Stop
            if (([guid]$partition.Guid).ToString('D') -ne '{{expectedGuid:D}}' -or $partition.GptType.Trim('{}').ToLowerInvariant() -ne '{{EspGptType}}') {
                throw 'ESP identity verification failed.'
            }
            $letter = [string]$partition.DriveLetter
            $added = $false
            if ([string]::IsNullOrWhiteSpace($letter) -or [int][char]$letter[0] -eq 0) {
                Set-Partition -InputObject $partition -NewDriveLetter '{{candidateLetter}}' -ErrorAction Stop | Out-Null
                $letter = '{{candidateLetter}}'
                $added = $true
            }
            [Console]::WriteLine("$letter|$([int]$added)")
            """;

        var result = await RunPowerShellAsync(script, cancellationToken);
        var resultLine = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        var fields = resultLine?.Split('|');
        if (fields is not { Length: 2 } || fields[0].Length != 1 || !char.IsLetter(fields[0][0]) || !bool.TryParse(fields[1] == "1" ? "true" : "false", out var added))
        {
            throw new InvalidOperationException("Windows did not return a usable drive letter for the verified ESP.");
        }

        var rootPath = $"{char.ToUpperInvariant(fields[0][0])}:\\";
        for (var attempt = 0; attempt < 20 && !Directory.Exists(rootPath); attempt++)
        {
            await Task.Delay(250, cancellationToken);
        }

        if (!Directory.Exists(rootPath))
        {
            if (added)
            {
                await RemoveTemporaryDriveLetterAsync(diskNumber, partitionNumber, expectedGuid, char.ToUpperInvariant(fields[0][0]));
            }
            throw new InvalidOperationException("Windows did not mount the verified ESP.");
        }

        return (rootPath, added ? char.ToUpperInvariant(fields[0][0]) : null);
    }

    private static async Task RemoveTemporaryDriveLetterAsync(int diskNumber, int partitionNumber, Guid expectedGuid, char driveLetter)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $partition = Get-Partition -DiskNumber {{diskNumber}} -PartitionNumber {{partitionNumber}} -ErrorAction Stop
            if (([guid]$partition.Guid).ToString('D') -eq '{{expectedGuid:D}}') {
                Remove-PartitionAccessPath -DiskNumber {{diskNumber}} -PartitionNumber {{partitionNumber}} -AccessPath '{{driveLetter}}:\' -ErrorAction Stop
            }
            """;

        try
        {
            await RunPowerShellAsync(script, CancellationToken.None);
        }
        catch
        {
            // Leave the mounted ESP accessible if Windows refuses removal; it does not affect its contents.
        }
    }

    private static char FindFreeDriveLetter()
    {
        var usedLetters = DriveInfo.GetDrives()
            .Where(drive => !string.IsNullOrEmpty(drive.Name))
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();

        foreach (var letter in "WXYZVUTSRQPONMLKJIHGF")
        {
            if (!usedLetters.Contains(letter) && !Directory.Exists($"{letter}:\\"))
            {
                return letter;
            }
        }

        throw new InvalidOperationException("No free drive letter is available to mount the staged ESP temporarily.");
    }

    private static async Task<(string Output, string Error)> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedScript);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start PowerShell to access the verified ESP.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"PowerShell could not access the verified ESP (exit code {process.ExitCode})."
                : $"PowerShell could not access the verified ESP: {error.Trim()}");
        }

        return (output, error);
    }
}
