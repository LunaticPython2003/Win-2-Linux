using System.Management;
using System.Text.Json.Serialization;

namespace Win2Linux.Core.Models;

/// <summary>
/// User-configurable settings for the Linux installer environment (locale, keyboard, timezone, user account, kernel, drivers, and confirmation mode).
/// </summary>
public record LinuxSetupConfiguration(
    string Locale = "en_US.UTF-8",
    string KeyboardLayout = "us",
    string Timezone = "UTC",
    string Username = "user",
    string RealName = "Linux User",
    string Password = "changeme",
    string Hostname = "fedora-dualboot",
    string CustomKernelArgs = "",
    bool SafeGraphics = false,
    bool InteractiveReview = true,
    bool InstallNvidiaDrivers = false,
    bool InstallProprietaryCodecs = true,
    string KernelSelection = "linux-default",
    string? DetectedGpu = null,
    string? DetectedHardwareModel = null
)
{
    /// <summary>
    /// Gets a default instance configured with the current system environment, detecting GPU and computer hardware.
    /// </summary>
    public static LinuxSetupConfiguration CreateDefault(string distroId = "fedora")
    {
        var username = Environment.UserName.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(username) || username == "root" || username == "administrator")
        {
            username = "user";
        }
        else
        {
            // Sanitize for Linux username rules: lowercase letters, digits, underscores, hyphens
            username = new string(username.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
            if (string.IsNullOrEmpty(username) || !char.IsLetter(username[0]))
            {
                username = "user";
            }
        }

        var hostname = $"{distroId}-dualboot";

        // Attempt to detect current Windows timezone id and map to standard IANA timezone
        var localZone = TimeZoneInfo.Local;
        var ianaTimezone = localZone.StandardName;
        try
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(localZone.Id, out var converted))
            {
                ianaTimezone = converted;
            }
        }
        catch
        {
            ianaTimezone = "UTC";
        }

        // Hardware detection for GPU and Computer Model (e.g. NVIDIA RTX & Lenovo Legion)
        string? detectedGpu = null;
        bool hasNvidia = false;
        string? detectedModel = null;
        bool isLegion = false;

        try
        {
            var scope = new ManagementScope(@"\\.\ROOT\cimv2");
            scope.Connect();

            using var gpuSearcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Name FROM Win32_VideoController"));
            using var gpuResults = gpuSearcher.Get();
            foreach (ManagementObject obj in gpuResults)
            {
                var name = obj["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    {
                        detectedGpu = name.Trim();
                        hasNvidia = true;
                    }
                    else if (detectedGpu == null)
                    {
                        detectedGpu = name.Trim();
                    }
                }
            }

            using var sysSearcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Model, Manufacturer FROM Win32_ComputerSystem"));
            using var sysResults = sysSearcher.Get();
            foreach (ManagementObject obj in sysResults)
            {
                var model = obj["Model"]?.ToString()?.Trim();
                var mfr = obj["Manufacturer"]?.ToString()?.Trim();
                detectedModel = $"{mfr} {model}".Trim();

                if ((model != null && (model.Contains("Legion", StringComparison.OrdinalIgnoreCase) || model.Equals("83F5", StringComparison.OrdinalIgnoreCase))) ||
                    (mfr != null && mfr.Contains("Lenovo", StringComparison.OrdinalIgnoreCase)))
                {
                    isLegion = true;
                }
            }
        }
        catch
        {
            // Graceful fallback if WMI is restricted or unavailable
        }

        var kernelSelection = isLegion ? "linux-7.3-legion" : "linux-default";

        return new LinuxSetupConfiguration(
            Locale: "en_US.UTF-8",
            KeyboardLayout: "us",
            Timezone: string.IsNullOrWhiteSpace(ianaTimezone) ? "UTC" : ianaTimezone,
            Username: username,
            RealName: Environment.UserName,
            Password: "changeme",
            Hostname: hostname,
            CustomKernelArgs: "",
            SafeGraphics: false,
            InteractiveReview: true,
            InstallNvidiaDrivers: hasNvidia,
            InstallProprietaryCodecs: true,
            KernelSelection: kernelSelection,
            DetectedGpu: detectedGpu,
            DetectedHardwareModel: detectedModel
        );
    }
}
