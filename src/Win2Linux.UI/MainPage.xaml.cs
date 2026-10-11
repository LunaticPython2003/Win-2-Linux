using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Win2Linux.Core.Boot;
using Win2Linux.Core.Distros;
using Win2Linux.Core.Models;
using Win2Linux.Core.Networking;
using Win2Linux.Core.Orchestrator;
using Win2Linux.Core.Storage;
using Windows.UI;

namespace Win2Linux_UI;

public sealed partial class MainPage : Page
{
    private DiscoveryReport? _discoveryReport;
    private DistroProfile _selectedDistro;
    private CandidateTarget? _activeTarget;
    private readonly WifiProfileService _wifiProfileService = new();
    private SavedWifiProfile? _selectedWifiProfile;
    private WifiNetworkConfiguration? _selectedWifiCredentials;
    private bool _isUpdatingWifiOptions;
    private bool _isShowingDistroWarning;
    private int _wifiSelectionVersion;

    private double _totalDiskGb = 953;
    private double _usedDataGb = 201;
    private double _freeDataGb = 752;

    public MainPage()
    {
        InitializeComponent();
        CmbWifiProfile.SelectedIndex = 0;
        _selectedDistro = DistroRegistry.SupportedDistributions[0]; // Ubuntu default
        Loaded += MainPage_Loaded;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Log("MainPage_Loaded fired");
        InitializeSetupDefaults();
        PanelWifiConfig.Visibility = _selectedDistro.Id is "ubuntu" or "zorin" ? Visibility.Visible : Visibility.Collapsed;
        await LoadDiscoveryDataAsync();
        await RefreshWifiProfilesAsync();
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
                TxtProtectedDiskSub.Text = $"Contains {_discoveryReport.ProtectedSystemDisk.SystemVolume} ({sysDisk.WindowsSerialNumber}). Strictly locked — will never be resized, partitioned, or modified.";
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

                var existingEsp = _discoveryReport.ExistingEsps.FirstOrDefault(e => e.DiskNumber == _activeTarget.DiskNumber && !e.IsOnSystemDisk);
                BtnCleanEsp.Visibility = existingEsp != null ? Visibility.Visible : Visibility.Collapsed;

                UpdateDiskProportions(DiskShrinkSlider.Value);
            }
            Log("LoadDiscoveryDataAsync completed successfully.");
        }
        catch (Exception ex)
        {
            Log($"LoadDiscoveryDataAsync ERROR: {ex}");
        }
    }

    private void MainScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LayoutRoot == null || ConfigurationGrid == null) return;

        var horizontalMargin = e.NewSize.Width < 720 ? 20 : 36;
        LayoutRoot.Margin = new Thickness(horizontalMargin, 20, horizontalMargin, 40);
        var contentWidth = Math.Max(0, e.NewSize.Width - horizontalMargin * 2 - 16);

        // Measure against the viewport so changing screens cannot leave an old content width behind.
        var cardColumns = contentWidth >= 1040 ? 4 : contentWidth >= 600 ? 2 : 1;
        ArrangeGrid(DistroGrid, cardColumns);
        ArrangeGrid(PreflightGrid, cardColumns);
        ArrangeGrid(ConfigurationGrid, contentWidth >= 800 ? 2 : 1);

        var inlineHeader = contentWidth >= 900;
        SetupHeader.ColumnDefinitions[1].Width = inlineHeader ? GridLength.Auto : new GridLength(0);
        Grid.SetColumn(BtnRescan, inlineHeader ? 1 : 0);
        Grid.SetRow(BtnRescan, inlineHeader ? 0 : 1);
        BtnRescan.HorizontalAlignment = inlineHeader ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    private static void ArrangeGrid(Grid grid, int columns)
    {
        if (grid.ColumnDefinitions.Count != columns)
        {
            grid.ColumnDefinitions.Clear();
            for (var column = 0; column < columns; column++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var rows = (grid.Children.Count + columns - 1) / columns;
        if (grid.RowDefinitions.Count != rows)
        {
            grid.RowDefinitions.Clear();
            for (var row = 0; row < rows; row++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var index = 0; index < grid.Children.Count; index++)
        {
            if (grid.Children[index] is FrameworkElement child)
            {
                Grid.SetColumn(child, index % columns);
                Grid.SetRow(child, index / columns);
            }
        }
    }

    private async void SelectDistro_Tapped(object sender, TappedRoutedEventArgs e)
    {
        try
        {
            if (_isShowingDistroWarning) return;
            if (sender is not Border tappedBorder || tappedBorder.Tag is not string distroId) return;

            var distro = DistroRegistry.SupportedDistributions.FirstOrDefault(d => d.Id == distroId);
            if (distro == null) return;

            _selectedDistro = distro;
            PanelWifiConfig.Visibility = distroId is "ubuntu" or "zorin" ? Visibility.Visible : Visibility.Collapsed;
            BtnUpdateExistingWifi.Visibility = distroId == "ubuntu" ? Visibility.Visible : Visibility.Collapsed;

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

            if (TxtHostname != null && (TxtHostname.Text.EndsWith("-dualboot") || TxtHostname.Text.EndsWith("-pc")))
            {
                TxtHostname.Text = $"{distro.Id}-dualboot";
            }

            var warning = distroId switch
            {
                "fedora" => "Fedora autoinstall support is approximately 60% complete. Its Kickstart setup is partially implemented and still needs refinement. Installation may require manual steps after reboot and is not yet fully verified.",
                "linuxmint" => "Linux Mint autoinstall support is approximately 50% complete. It uses the same Ubiquity/preseed installer configuration approach as Zorin, so it should work, but an end-to-end Linux Mint installation has not been verified. Manual steps may be required after reboot.",
                _ => null
            };

            if (warning != null && XamlRoot != null)
            {
                _isShowingDistroWarning = true;
                try
                {
                    var dialog = new ContentDialog
                    {
                        Title = $"{distro.DisplayName}: autoinstall warning",
                        Content = warning,
                        CloseButtonText = "Understood",
                        XamlRoot = XamlRoot
                    };
                    await dialog.ShowAsync();
                }
                finally
                {
                    _isShowingDistroWarning = false;
                }
            }
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

        if (_selectedDistro.Id is "ubuntu" or "zorin" && _selectedWifiProfile != null && _selectedWifiCredentials == null)
        {
            var wifiDialog = new ContentDialog
            {
                Title = "Wi-Fi profile is not ready",
                Content = "Win2Linux could not read the selected saved network key. Refresh and select it again, or choose 'Do not configure Wi-Fi' and use a wired connection during installation.",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await wifiDialog.ShowAsync();
            return;
        }

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
        // Apply the new view's layout before resetting the scroll position.
        LayoutRoot.UpdateLayout();
        MainScrollViewer.ChangeView(0, 0, 1, disableAnimation: true);

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

        var setupConfig = BuildSetupConfigurationFromUi();

        var plan = new InstallationPlan(
            _selectedDistro,
            _activeTarget,
            _discoveryReport.ProtectedSystemDisk,
            reservedBytes,
            linuxSizeGb,
            setupConfig
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

    private async void CleanEsp_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTarget == null || _discoveryReport == null || this.XamlRoot == null) return;

        var confirmDialog = new ContentDialog
        {
            Title = "Delete ESP Partition & Restore Drive?",
            Content = $"This will safely delete the staged EFI System Partition from Disk {_activeTarget.DiskNumber} and extend volume {_activeTarget.DriveLetter} to reclaim all disk space.\n\nAre you sure you want to proceed?",
            PrimaryButtonText = "Delete ESP",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await confirmDialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            try
            {
                BtnCleanEsp.IsEnabled = false;
                Log($"Deleting ESP partition on Disk {_activeTarget.DiskNumber} and extending {_activeTarget.DriveLetter}...");

                var removed = await EspCreationService.DeleteEspAndExtendVolumeAsync(
                    _activeTarget.DiskNumber,
                    _discoveryReport.ProtectedSystemDisk.DiskNumber,
                    _activeTarget.DriveLetter);

                if (removed)
                {
                    Log("ESP partition deleted and volume extended successfully.");
                }
                else
                {
                    Log("No ESP partition found to delete on target disk.");
                }

                await LoadDiscoveryDataAsync();
            }
            catch (Exception ex)
            {
                Log($"Failed to delete ESP partition: {ex.Message}");
            }
            finally
            {
                BtnCleanEsp.IsEnabled = true;
            }
        }
    }

    private void InitializeSetupDefaults()
    {
        try
        {
            var defaults = LinuxSetupConfiguration.CreateDefault(_selectedDistro?.Id ?? "fedora");
            TxtUsername.Text = defaults.Username;
            TxtRealName.Text = defaults.RealName;
            TxtPassword.Password = "changeme";
            TxtHostname.Text = defaults.Hostname;

            // Attempt to auto-select detected timezone
            foreach (ComboBoxItem item in CmbTimezone.Items)
            {
                if (item.Tag?.ToString() == defaults.Timezone || item.Content?.ToString()?.Contains(defaults.Timezone) == true)
                {
                    CmbTimezone.SelectedItem = item;
                    break;
                }
            }

            // Hardware detection display: GPU and Legion
            if (!string.IsNullOrWhiteSpace(defaults.DetectedGpu))
            {
                BadgeGpuDetected.Visibility = Visibility.Visible;
                TxtGpuDetected.Text = $"{defaults.DetectedGpu} Detected";
                ChkInstallNvidia.IsChecked = defaults.InstallNvidiaDrivers;
            }

            if (defaults.DetectedHardwareModel != null &&
                (defaults.DetectedHardwareModel.Contains("Legion", StringComparison.OrdinalIgnoreCase) ||
                 defaults.DetectedHardwareModel.Contains("83F5", StringComparison.OrdinalIgnoreCase) ||
                 defaults.DetectedHardwareModel.Contains("Lenovo", StringComparison.OrdinalIgnoreCase)))
            {
                BadgeLegionDetected.Visibility = Visibility.Visible;
                TxtLegionDetected.Text = $"{defaults.DetectedHardwareModel} Detected — Linux 7.3 Kernel Recommended for Speaker Audio Fix";
            }

            // Select kernel
            foreach (ComboBoxItem item in CmbKernel.Items)
            {
                if (item.Tag?.ToString() == defaults.KernelSelection)
                {
                    CmbKernel.SelectedItem = item;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"InitializeSetupDefaults failed: {ex.Message}");
        }
    }

    private LinuxSetupConfiguration BuildSetupConfigurationFromUi()
    {
        var username = TxtUsername.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(username)) username = "user";

        var realName = string.IsNullOrWhiteSpace(TxtRealName.Text) ? "Linux User" : TxtRealName.Text.Trim();
        var password = string.IsNullOrWhiteSpace(TxtPassword.Password) ? "changeme" : TxtPassword.Password;
        var hostname = string.IsNullOrWhiteSpace(TxtHostname.Text) ? $"{_selectedDistro?.Id ?? "linux"}-dualboot" : TxtHostname.Text.Trim();

        var locale = (CmbLocale.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en_US.UTF-8";
        var keyboard = (CmbKeyboard.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "us";
        var timezone = (CmbTimezone.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "UTC";

        var interactiveReview = ChkInteractiveReview.IsChecked ?? true;
        var safeGraphics = ChkSafeGraphics.IsChecked ?? false;
        var customKernelArgs = TxtKernelArgs.Text.Trim();

        var installNvidia = ChkInstallNvidia.IsChecked ?? false;
        var installCodecs = ChkInstallCodecs.IsChecked ?? true;
        var kernelSelection = (CmbKernel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "linux-default";

        return new LinuxSetupConfiguration(
            Locale: locale,
            KeyboardLayout: keyboard,
            Timezone: timezone,
            Username: username,
            RealName: realName,
            Password: password,
            Hostname: hostname,
            CustomKernelArgs: customKernelArgs,
            SafeGraphics: safeGraphics,
            InteractiveReview: interactiveReview,
            InstallNvidiaDrivers: installNvidia,
            InstallProprietaryCodecs: installCodecs,
            KernelSelection: kernelSelection,
            WifiNetwork: _selectedDistro?.Id is "ubuntu" or "zorin" ? _selectedWifiCredentials : null
        );
    }

    private async void RefreshWifiProfiles_Click(object sender, RoutedEventArgs e)
    {
        await RefreshWifiProfilesAsync();
    }

    private async Task RefreshWifiProfilesAsync()
    {
        var previousProfile = _selectedWifiProfile;
        BtnRefreshWifi.IsEnabled = false;
        TxtWifiStatus.Text = "Loading saved Wi-Fi networks from Windows…";
        _isUpdatingWifiOptions = true;

        try
        {
            var profiles = await Task.Run(() => _wifiProfileService.GetSavedProfiles());
            CmbWifiProfile.Items.Clear();
            CmbWifiProfile.Items.Add(new ComboBoxItem { Content = "Do not configure Wi-Fi" });

            ComboBoxItem? previousItem = null;
            foreach (var profile in profiles)
            {
                var item = new ComboBoxItem
                {
                    Content = $"{profile.Ssid} — {profile.InterfaceDescription}",
                    Tag = profile
                };
                CmbWifiProfile.Items.Add(item);
                if (previousProfile != null &&
                    profile.InterfaceId == previousProfile.InterfaceId &&
                    profile.ProfileName.Equals(previousProfile.ProfileName, StringComparison.Ordinal))
                {
                    previousItem = item;
                }
            }

            CmbWifiProfile.SelectedItem = previousItem ?? CmbWifiProfile.Items[0];
            _isUpdatingWifiOptions = false;

            if (previousItem?.Tag is SavedWifiProfile savedProfile)
            {
                await LoadSelectedWifiProfileAsync(savedProfile);
            }
            else
            {
                _wifiSelectionVersion++;
                _selectedWifiProfile = null;
                _selectedWifiCredentials = null;
                BtnUpdateExistingWifi.IsEnabled = false;
                TxtWifiStatus.Text = profiles.Count == 0
                    ? "No saved Wi-Fi networks were found. Wi-Fi is optional; Ethernet works without this setting."
                    : "Select a saved network for the installer, or leave Wi-Fi disabled.";
            }
        }
        catch (Exception ex)
        {
            _isUpdatingWifiOptions = false;
            _wifiSelectionVersion++;
            _selectedWifiProfile = null;
            _selectedWifiCredentials = null;
            BtnUpdateExistingWifi.IsEnabled = false;
            _isUpdatingWifiOptions = true;
            CmbWifiProfile.Items.Clear();
            CmbWifiProfile.Items.Add(new ComboBoxItem { Content = "Do not configure Wi-Fi" });
            CmbWifiProfile.SelectedIndex = 0;
            _isUpdatingWifiOptions = false;
            TxtWifiStatus.Text = $"Could not read Windows Wi-Fi profiles: {ex.Message}";
            Log($"RefreshWifiProfilesAsync failed: {ex.Message}");
        }
        finally
        {
            _isUpdatingWifiOptions = false;
            BtnRefreshWifi.IsEnabled = true;
        }
    }

    private async void WifiProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingWifiOptions)
        {
            return;
        }

        if (CmbWifiProfile.SelectedItem is ComboBoxItem { Tag: SavedWifiProfile profile })
        {
            await LoadSelectedWifiProfileAsync(profile);
            return;
        }

        _wifiSelectionVersion++;
        _selectedWifiProfile = null;
        _selectedWifiCredentials = null;
        BtnUpdateExistingWifi.IsEnabled = false;
        TxtWifiStatus.Text = "Wi-Fi will not be configured. Ethernet works without this setting.";
    }

    private async Task LoadSelectedWifiProfileAsync(SavedWifiProfile profile)
    {
        var requestVersion = ++_wifiSelectionVersion;
        _selectedWifiProfile = profile;
        _selectedWifiCredentials = null;
        BtnUpdateExistingWifi.IsEnabled = false;
        TxtWifiStatus.Text = "Reading the selected saved network key from Windows…";

        try
        {
            var credentials = await Task.Run(() => _wifiProfileService.ReadSelectedCredentials(profile));
            if (requestVersion != _wifiSelectionVersion)
            {
                return;
            }

            _selectedWifiCredentials = new WifiNetworkConfiguration(
                credentials.Ssid,
                credentials.Password,
                credentials.IsOpenNetwork);
            BtnUpdateExistingWifi.IsEnabled = true;
            TxtWifiStatus.Text = credentials.IsOpenNetwork
                ? $"Open network '{credentials.Ssid}' selected. The installer will connect without a password."
                : $"Saved key loaded for '{credentials.Ssid}'. It will be added to the installer and installed system's Wi-Fi configuration.";
        }
        catch (Exception ex)
        {
            if (requestVersion != _wifiSelectionVersion)
            {
                return;
            }

            TxtWifiStatus.Text = ex.Message;
            Log($"Reading selected Windows Wi-Fi profile failed: {ex.Message}");
            BtnUpdateExistingWifi.IsEnabled = false;
        }
    }

    private async void UpdateExistingWifi_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWifiCredentials == null)
        {
            TxtWifiStatus.Text = "Select a saved network and wait for its key to load first.";
            return;
        }

        BtnUpdateExistingWifi.IsEnabled = false;
        TxtWifiStatus.Text = "Updating the verified existing Ubuntu installer on its EFI partition…";
        try
        {
            await WifiAutoinstallUpdater.UpdateExistingUbuntuAutoinstallAsync(_selectedWifiCredentials);
            TxtWifiStatus.Text = "Existing Ubuntu autoinstall updated. Restart and select the existing Win2Linux/Ubuntu boot entry to retry; disk partitions were not changed.";
        }
        catch (Exception ex)
        {
            TxtWifiStatus.Text = ex.Message;
            Log($"Updating existing Ubuntu Wi-Fi config failed: {ex.Message}");
        }
        finally
        {
            BtnUpdateExistingWifi.IsEnabled = _selectedWifiCredentials != null;
        }
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
