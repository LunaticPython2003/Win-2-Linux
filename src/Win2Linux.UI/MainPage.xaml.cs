using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Win2Linux.Core.Distros;
using Win2Linux.Core.Models;
using Win2Linux.Core.Orchestrator;
using Win2Linux.Core.Storage;
using Windows.UI;

namespace Win2Linux_UI;

public sealed partial class MainPage : Page
{
    private DiscoveryReport? _discoveryReport;
    private DistroProfile _selectedDistro;
    private CandidateTarget? _activeTarget;

    private double _totalDiskGb = 953;
    private double _usedDataGb = 201;
    private double _freeDataGb = 752;

    public MainPage()
    {
        InitializeComponent();
        _selectedDistro = DistroRegistry.SupportedDistributions[0]; // Ubuntu default
        Loaded += MainPage_Loaded;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Log("MainPage_Loaded fired");
        await LoadDiscoveryDataAsync();
    }

    private async void RefreshDiscovery_Click(object sender, RoutedEventArgs e)
    {
        await LoadDiscoveryDataAsync();
    }

    private async Task LoadDiscoveryDataAsync()
    {
        try
        {
            Log("LoadDiscoveryDataAsync starting discovery query...");
            _discoveryReport = await StorageDiscoveryService.RunDiscoveryAsync();
            Log("Storage discovery query completed.");

            // 1. Firmware & Security Badges
            TxtFirmwareMode.Text = _discoveryReport.Firmware.FirmwareType;
            TxtSecureBoot.Text = _discoveryReport.Firmware.SecureBootEnabled switch
            {
                true => "Active & Enforced",
                false => "Disabled in BIOS",
                null => "Not Detected"
            };

            TxtProtectionStatus.Text = $"Disk {_discoveryReport.ProtectedSystemDisk.DiskNumber} ({_discoveryReport.ProtectedSystemDisk.SystemVolume}) Locked";
            TxtWslStatus.Text = _discoveryReport.Wsl.IsInstalled ? "WSL2 Ready" : "Not Installed";

            // 2. Protected System Disk
            var sysDisk = _discoveryReport.Disks.FirstOrDefault(d => d.IsSystemDisk);
            if (sysDisk != null)
            {
                var sysGb = sysDisk.TotalBytes / (1024 * 1024 * 1024);
                TxtProtectedDiskTitle.Text = $"Disk {sysDisk.DiskNumber} — {sysDisk.FriendlyName} ({sysGb} GB {sysDisk.BusType})";
                TxtProtectedDiskSub.Text = $"Contains {_discoveryReport.ProtectedSystemDisk.SystemVolume} ({sysDisk.SerialNumber}). Strictly locked — will never be resized, partitioned, or modified.";
            }

            // 3. Target Secondary SSD
            _activeTarget = _discoveryReport.CandidateTargets.FirstOrDefault(t => t.IsEligible);
            if (_activeTarget != null)
            {
                var targetDisk = _discoveryReport.Disks.FirstOrDefault(d => d.DiskNumber == _activeTarget.DiskNumber);
                var targetGb = _activeTarget.TotalBytes / (1024 * 1024 * 1024);
                var freeGb = _activeTarget.FreeBytes / (1024 * 1024 * 1024);
                var maxShrinkGb = _activeTarget.MaxShrinkBytes / (1024 * 1024 * 1024);

                _totalDiskGb = targetGb;
                _freeDataGb = freeGb;
                _usedDataGb = targetGb > freeGb ? targetGb - freeGb : 10;

                TxtTargetDiskTitle.Text = $"Disk {_activeTarget.DiskNumber} — {targetDisk?.FriendlyName ?? "Secondary SSD"} ({targetGb} GB {targetDisk?.BusType})";
                TxtTargetDiskSub.Text = $"Target Volume: {_activeTarget.DriveLetter} [{_activeTarget.VolumeLabel}] (NTFS) • Total: {targetGb} GB • Free Space: {freeGb} GB";

                DiskShrinkSlider.Minimum = 30;
                DiskShrinkSlider.Maximum = Math.Max(30, maxShrinkGb);
                DiskShrinkSlider.Value = Math.Min(450, maxShrinkGb);

                TxtMaxShrink.Text = $"Max: {maxShrinkGb} GB (Preserves 20 GB Windows buffer)";

                UpdateDiskProportions(DiskShrinkSlider.Value);
            }
            Log("LoadDiscoveryDataAsync completed successfully.");
        }
        catch (Exception ex)
        {
            Log($"LoadDiscoveryDataAsync ERROR: {ex}");
        }
    }

    private void SelectDistro_Tapped(object sender, TappedRoutedEventArgs e)
    {
        try
        {
            if (sender is not Border tappedBorder || tappedBorder.Tag is not string distroId) return;

            var distro = DistroRegistry.SupportedDistributions.FirstOrDefault(d => d.Id == distroId);
            if (distro == null) return;

            _selectedDistro = distro;

            // Reset all card borders
            var defaultStroke = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            CardUbuntu.BorderBrush = defaultStroke;
            CardUbuntu.BorderThickness = new Thickness(1);
            CardFedora.BorderBrush = defaultStroke;
            CardFedora.BorderThickness = new Thickness(1);
            CardLinuxMint.BorderBrush = defaultStroke;
            CardLinuxMint.BorderThickness = new Thickness(1);
            CardZorin.BorderBrush = defaultStroke;
            CardZorin.BorderThickness = new Thickness(1);

            // Highlight selected
            var accentBrush = GetBrushFromHex(distro.AccentColor);
            tappedBorder.BorderBrush = accentBrush;
            tappedBorder.BorderThickness = new Thickness(2);

            // Handle Fedora Desktop Environment selection panel
            if (distroId == "fedora")
            {
                PanelFedoraDeSelection.Visibility = Visibility.Visible;
                _selectedDistro = distro.WithDesktopEnvironment(distro.SelectedDesktopEnvironment ?? "kde");
                var deName = _selectedDistro.SelectedDesktopEnvironment == "gnome" ? "GNOME" : "KDE Plasma";
                BtnInstallText.Text = $"Prepare and Stage Fedora 44 ({deName})";
            }
            else
            {
                PanelFedoraDeSelection.Visibility = Visibility.Collapsed;
                BtnInstallText.Text = $"Prepare and Stage {distro.DisplayName}";
            }

            // Update partition visualizer accent color
            BarLinuxSpace.Background = accentBrush;
            LegendLinuxColor.Background = accentBrush;
            LegendLinuxText.Text = $"{distro.DisplayName.Split(' ')[0]} Space ({distro.DefaultFilesystem})";
            TxtSliderValue.Foreground = accentBrush;
        }
        catch (Exception ex)
        {
            Log($"SelectDistro_Tapped ERROR: {ex}");
        }
    }

    private void SelectFedoraDE_Tapped(object sender, TappedRoutedEventArgs e)
    {
        try
        {
            if (sender is not Border tappedBorder || tappedBorder.Tag is not string deId) return;

            if (_selectedDistro.Id == "fedora")
            {
                _selectedDistro = _selectedDistro.WithDesktopEnvironment(deId);
            }

            var defaultStroke = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            CardDeKde.BorderBrush = defaultStroke;
            CardDeKde.BorderThickness = new Thickness(1);
            CardDeKde.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 34, 34, 34));
            BadgeDeKde.Text = "AVAILABLE";
            BadgeDeKdeBorder.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 60, 60, 60));

            CardDeGnome.BorderBrush = defaultStroke;
            CardDeGnome.BorderThickness = new Thickness(1);
            CardDeGnome.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 34, 34, 34));
            BadgeDeGnome.Text = "AVAILABLE";
            BadgeDeGnomeBorder.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 60, 60, 60));

            if (deId == "kde")
            {
                var plasmaBrush = GetBrushFromHex("#1D99F3");
                CardDeKde.BorderBrush = plasmaBrush;
                CardDeKde.BorderThickness = new Thickness(2);
                CardDeKde.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 34, 40, 58));
                BadgeDeKde.Text = "SELECTED";
                BadgeDeKdeBorder.Background = plasmaBrush;
                BtnInstallText.Text = "Prepare and Stage Fedora 44 (KDE Plasma)";
            }
            else
            {
                var gnomeBrush = GetBrushFromHex("#294172");
                CardDeGnome.BorderBrush = gnomeBrush;
                CardDeGnome.BorderThickness = new Thickness(2);
                CardDeGnome.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 41, 65, 114));
                BadgeDeGnome.Text = "SELECTED";
                BadgeDeGnomeBorder.Background = gnomeBrush;
                BtnInstallText.Text = "Prepare and Stage Fedora 44 (GNOME)";
            }
        }
        catch (Exception ex)
        {
            Log($"SelectFedoraDE_Tapped ERROR: {ex}");
        }
    }

    private void DiskShrinkSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        try
        {
            UpdateDiskProportions(e.NewValue);
        }
        catch (Exception ex)
        {
            Log($"DiskShrinkSlider_ValueChanged ERROR: {ex}");
        }
    }

    private void UpdateDiskProportions(double linuxGb)
    {
        if (ColUsedData == null || ColRemainingWindows == null || ColLinuxSpace == null) return;

        double usedGb = Math.Max(1.0, _usedDataGb);
        double remainingWindowsGb = Math.Max(1.0, _freeDataGb - linuxGb);
        double allocLinuxGb = Math.Max(1.0, linuxGb);

        ColUsedData.Width = new GridLength(usedGb, GridUnitType.Star);
        ColRemainingWindows.Width = new GridLength(remainingWindowsGb, GridUnitType.Star);
        ColLinuxSpace.Width = new GridLength(allocLinuxGb, GridUnitType.Star);

        TxtBarUsed.Text = $"Used Data: ~{_usedDataGb:F0} GB";
        TxtBarWindowsFree.Text = $"Windows Free: ~{Math.Max(0, _freeDataGb - linuxGb):F0} GB";
        TxtBarLinux.Text = $"{_selectedDistro?.DisplayName?.Split(' ')[0] ?? "Linux"}: {linuxGb:F0} GB";

        TxtSliderValue.Text = $"{linuxGb:F0} GB";
    }

    private async void StartInstallation_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTarget == null || _discoveryReport == null) return;

        var linuxSizeGb = DiskShrinkSlider.Value;
        var reservedBytes = (ulong)(linuxSizeGb * 1024 * 1024 * 1024);

        var summaryText = $"Pre-installation Plan:\n\n" +
                          $"• Selected Distribution: {_selectedDistro.DisplayName}\n" +
                          $"• Secure Boot Signing: {_selectedDistro.SecureBootStatus} ({_selectedDistro.SecureBootSigner})\n" +
                          $"• Target Physical Disk: Disk {_activeTarget.DiskNumber} ({_activeTarget.DriveLetter})\n" +
                          $"• Space to Shrink & Reserve: {linuxSizeGb:F0} GB\n" +
                          $"• Target Filesystem: {_selectedDistro.DefaultFilesystem}\n\n" +
                          $"Safety Assurance:\n" +
                          $"✓ Protected Windows System Disk (Disk {_discoveryReport.ProtectedSystemDisk.DiskNumber}) is completely untouched.\n" +
                          $"✓ Existing Windows boot files remain intact.\n" +
                          $"✓ Resizing is performed through Windows-supported storage management.\n\n" +
                          $"Would you like to proceed with real dual-boot space allocation and automated staging?";

        if (this.XamlRoot == null) return;

        var confirmDialog = new ContentDialog
        {
            Title = "Confirm Dual-Boot Space Allocation",
            Content = summaryText,
            PrimaryButtonText = "Confirm & Proceed",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        Log($"StartInstallation_Click: Prompting confirmation for {_selectedDistro.DisplayName} ({linuxSizeGb:F0} GB on {_activeTarget.DriveLetter})...");
        var result = await confirmDialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            Log("StartInstallation_Click: User cancelled confirmation dialog.");
            return;
        }

        Log("StartInstallation_Click: User confirmed. Switching to active StagingView...");

        // Instantly switch view to dedicated Staging Dashboard
        SetupView.Visibility = Visibility.Collapsed;
        StagingView.Visibility = Visibility.Visible;
        MainScrollViewer.ChangeView(null, 0, null);

        TxtStagingTitle.Text = $"Staging {_selectedDistro.DisplayName}...";
        TxtTargetDistro.Text = _selectedDistro.DisplayName;
        TxtTargetStorage.Text = $"Disk {_activeTarget.DiskNumber} ({_activeTarget.DriveLetter}) • {linuxSizeGb:F0} GB";
        TxtCurrentStepTitle.Text = "Step 1 of 10: Verifying System Disk Safety";
        TxtStatusBadge.Text = "IN PROGRESS";
        TxtStatusBadge.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212));
        StagingRing.IsActive = true;
        RebootCard.Visibility = Visibility.Collapsed;
        TxtLiveLogs.Text = string.Empty;
        StagingProgressBar.Value = 0;
        TxtStagingPercent.Text = "0%";
        DownloadProgressCard.Visibility = Visibility.Collapsed;
        DownloadProgressBar.Value = 0;
        TxtDownloadSize.Text = "0 MB / 0 MB";
        ResetStepPills();

        var plan = new InstallationPlan(
            _selectedDistro,
            _activeTarget,
            _discoveryReport.ProtectedSystemDisk,
            reservedBytes,
            linuxSizeGb
        );

        var progressTracker = new Progress<ProgressUpdate>(update =>
        {
            TxtCurrentStepTitle.Text = $"Step {update.StepIndex} of {update.TotalSteps}: {update.StepTitle}";
            StagingProgressBar.Value = update.Percent;
            TxtStagingPercent.Text = $"{update.Percent:F0}%";

            TxtLiveLogs.Text += $"[{DateTime.Now:HH:mm:ss}] {update.LogMessage}\n";
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);

            // Update step pill indicator
            if (update.StepIndex >= 1 && update.StepIndex <= 10)
            {
                UpdateStepPill(update.StepIndex, update.IsSuccess);
            }

            // Show/hide download sub-progress bar during ISO download step
            if (update.StepIndex == 3 && !update.IsComplete)
            {
                DownloadProgressCard.Visibility = Visibility.Visible;
                // Parse "X MB / Y MB" from log message to update the download bar
                var mbMatch = System.Text.RegularExpressions.Regex.Match(
                    update.LogMessage, @"(\d+(?:\.\d+)?)\s*MB\s*/\s*(\d+(?:\.\d+)?)\s*MB");
                if (mbMatch.Success &&
                    double.TryParse(mbMatch.Groups[1].Value, out double mbDown) &&
                    double.TryParse(mbMatch.Groups[2].Value, out double mbTotal) &&
                    mbTotal > 0)
                {
                    DownloadProgressBar.Value = mbDown / mbTotal * 100.0;
                    TxtDownloadSize.Text = $"{mbDown:F0} MB / {mbTotal:F0} MB";
                }
            }
            else if (update.StepIndex > 3)
            {
                DownloadProgressCard.Visibility = Visibility.Collapsed;
            }

            if (update.IsComplete)
            {
                StagingRing.IsActive = false;
                DownloadProgressCard.Visibility = Visibility.Collapsed;
                if (update.IsSuccess)
                {
                    TxtStatusBadge.Text = "COMPLETED";
                    TxtStatusBadge.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 65));
                    TxtStagingTitle.Text = "Dual-Boot Staging Succeeded!";
                    RebootCard.Visibility = Visibility.Visible;
                    // Light all pills green
                    for (int i = 1; i <= 9; i++) UpdateStepPill(i, true);
                    Log("Staging completed successfully.");
                }
                else
                {
                    TxtStatusBadge.Text = "FAILED";
                    TxtStatusBadge.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 216, 59, 1));
                    TxtStagingTitle.Text = "Staging Encountered An Issue";
                    Log($"Staging failed: {update.ErrorMessage}");
                }
            }
        });

        await InstallationOrchestrator.ExecuteStagingAsync(plan, progressTracker);
    }

    private async void RebootNow_Click(object sender, RoutedEventArgs e)
    {
        if (this.XamlRoot == null) return;

        var rebootConfirm = new ContentDialog
        {
            Title = "Reboot into Automated Linux Setup?",
            Content = "Your system will reboot in 10 seconds to launch the automated dual-boot installer.\n\nPlease save any open files in other apps before continuing.",
            PrimaryButtonText = "Reboot Now",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var res = await rebootConfirm.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/r /t 10 /c \"Win2Linux: Rebooting system to start automated Linux installation\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                Log($"Reboot execution exception: {ex}");
            }
        }
    }

    private async void RebootLater_Click(object sender, RoutedEventArgs e)
    {
        if (this.XamlRoot == null) return;

        var laterDialog = new ContentDialog
        {
            Title = "Staging Ready for Later Reboot",
            Content = "All unattended files, kernel payloads, and EFI bootloader entries have been staged on your secondary disk.\n\nWhenever you are ready, restart your PC and select the dual-boot entry from your UEFI boot menu.",
            CloseButtonText = "Got it",
            XamlRoot = this.XamlRoot
        };

        await laterDialog.ShowAsync();
    }

    private static SolidColorBrush GetBrushFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, null, out byte r) &&
            byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, null, out byte g) &&
            byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, null, out byte b))
        {
            return new SolidColorBrush(Color.FromArgb(255, r, g, b));
        }
        return new SolidColorBrush(Colors.DodgerBlue);
    }

    private void ResetStepPills()
    {
        var gray = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 51, 51, 51));
        Pill1.Background = Pill2.Background = Pill3.Background = Pill4.Background =
        Pill5.Background = Pill6.Background = Pill7.Background = Pill8.Background =
        Pill9.Background = gray;
    }

    private void UpdateStepPill(int stepIndex, bool success)
    {
        var activeBrush = new SolidColorBrush(success
            ? Windows.UI.Color.FromArgb(255, 16, 124, 65)   // green — completed
            : Windows.UI.Color.FromArgb(255, 0, 120, 212));  // blue — in progress
        var failBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 216, 59, 1)); // red

        var pill = stepIndex switch
        {
            1 => Pill1, 2 => Pill2, 3 => Pill3, 4 => Pill4, 5 => Pill5,
            6 => Pill6, 7 => Pill7, 8 => Pill8, 9 => Pill9,
            _ => null
        };

        if (pill != null) pill.Background = activeBrush;
    }

    private static void Log(string message)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "debug.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }
}
