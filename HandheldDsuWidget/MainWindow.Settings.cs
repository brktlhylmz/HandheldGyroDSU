using System;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HandheldDsuServer.Models;
using HandheldDsuWidget.Services;

namespace HandheldDsuWidget;

public partial class MainWindow
{
    private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded)
            return;

        if (LanguageComboBox.SelectedValue is string langCode)
        {
            if (_lastStatus is not null)
            {
                _lastStatus.Language = langCode;
            }

            DsuStatusDto status = await _ipcClient.SetLanguageAsync(langCode);
            _loc.SetLanguage(langCode);
            UpdateUiFromStatus(status);
        }
    }

    private async void ApplyPortButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyPortChangeAsync();
    }

    private async void PortTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ApplyPortChangeAsync();
            Keyboard.ClearFocus();
        }
    }

    private async Task ApplyPortChangeAsync()
    {
        if (!int.TryParse(PortTextBox.Text.Trim(), out int newPort) || newPort is < 1024 or > 65535)
        {
            PortStatusSubtitle.Text = _loc["PortInvalidError"];
            PortStatusSubtitle.Foreground = StatusOfflineBrush;
            return;
        }

        ApplyPortButton.IsEnabled = false;
        try
        {
            var (success, status) = await _ipcClient.ChangePortAsync(newPort);
            if (success)
            {
                _lastSyncedServerPort = status.ServerPort;
                PortTextBox.Text = FormattableString.Invariant($"{status.ServerPort}");
                PortStatusSubtitle.Text = _loc.Format("PortSavedFormat", newPort);
                PortStatusSubtitle.Foreground = StatusOnlineBrush;
            }
            else
            {
                PortStatusSubtitle.Text = status.Message;
                PortStatusSubtitle.Foreground = StatusOfflineBrush;
            }
            UpdateUiFromStatus(status);
            RenderControllersAndRulesUi();
        }
        finally
        {
            ApplyPortButton.IsEnabled = true;
        }
    }

    private async void MultiPortClusterToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        bool enableCluster = MultiPortClusterToggle.IsChecked == true;
        _ = _ipcClient.SetSecondaryClusterActive(enableCluster);
        DsuStatusDto status = await _ipcClient.GetStatusAsync();
        UpdateUiFromStatus(status);
        RenderControllersAndRulesUi();
    }

    private async void ApplySecondaryPortButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplySecondaryPortChangeAsync();
    }

    private async void SecondaryPortTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ApplySecondaryPortChangeAsync();
            Keyboard.ClearFocus();
        }
    }

    private async Task ApplySecondaryPortChangeAsync()
    {
        int primaryPort = _lastStatus?.ServerPort ?? 26760;
        if (!int.TryParse(SecondaryPortTextBox.Text.Trim(), out int newSecPort) ||
            newSecPort is < 1024 or > 65535 ||
            newSecPort == primaryPort ||
            newSecPort == 26761)
        {
            SecondaryPortStatusSubtitle.Text = _loc["PortInvalidError"];
            SecondaryPortStatusSubtitle.Foreground = StatusOfflineBrush;
            return;
        }

        ApplySecondaryPortButton.IsEnabled = false;
        try
        {
            var (success, status) = await _ipcClient.ChangeSecondaryPortAsync(newSecPort);
            if (success)
            {
                _lastSyncedSecondaryPort = status.SecondaryPort;
                SecondaryPortTextBox.Text = FormattableString.Invariant($"{status.SecondaryPort}");
                SecondaryPortStatusSubtitle.Text = _loc.Format("SecondaryPortSavedFormat", newSecPort);
                SecondaryPortStatusSubtitle.Foreground = StatusOnlineBrush;
            }
            else
            {
                SecondaryPortStatusSubtitle.Text = status.Message;
                SecondaryPortStatusSubtitle.Foreground = StatusOfflineBrush;
            }
            UpdateUiFromStatus(status);
            RenderControllersAndRulesUi();
        }
        finally
        {
            ApplySecondaryPortButton.IsEnabled = true;
        }
    }

    private async void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        bool targetState = StartupToggle.IsChecked == true;
        DsuStatusDto status = await _ipcClient.SetStartupAsync(targetState);
        UpdateUiFromStatus(status);
    }

    private async void CloseToTrayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        DsuStatusDto status = await _ipcClient.UpdatePreferencesAsync(closeToTray: CloseToTrayToggle.IsChecked == true);
        UpdateUiFromStatus(status);
    }

    private async void GameBarOverlayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        DsuStatusDto status = await _ipcClient.UpdatePreferencesAsync(gameBarOverlayEnabled: GameBarOverlayToggle.IsChecked == true);
        UpdateUiFromStatus(status);
    }

    private async void HudNotificationsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        DsuStatusDto status = await _ipcClient.UpdatePreferencesAsync(hudNotificationsEnabled: HudNotificationsToggle.IsChecked == true);
        UpdateUiFromStatus(status);
    }

    private async void TabletopCalibrationGuardToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        DsuStatusDto status = await _ipcClient.UpdatePreferencesAsync(
            tabletopCalibrationGuard: TabletopCalibrationGuardToggle.IsChecked == true);
        UpdateUiFromStatus(status);
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppSettings.DataDirectoryPath,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore shell launch errors
        }
    }

    private async void ResetAllSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ResetAllSettingsButton.IsEnabled = false;
        try
        {
            _loopbackTester.Stop();
            _gyroMouseService.IsEnabled = false;
            _automationWatcher.ClearSimulatedControllers();
            _lastSyncedServerPort = -1;
            _lastSyncedSecondaryPort = -1;

            DsuStatusDto status = await _ipcClient.ResetAllSettingsAsync();
            _automationWatcher.Config.AutoSwitchByController = true;
            _automationWatcher.Config.AutoSwitchByEmulator = true;
            _automationWatcher.SaveConfig();

            _suppressUiEvents = true;
            DevGyroMouseToggle.IsChecked = false;
            DevGyroMouseModeComboBox.SelectedIndex = 0;
            DevGyroMouseSpeedSlider.Value = 1.2;
            AutoControllerSwitchToggle.IsChecked = true;
            AutoEmulatorSwitchToggle.IsChecked = true;
            PortStatusSubtitle.Text = _loc["PortDefaultDesc"];
            PortStatusSubtitle.Foreground = TextSecondaryBrush;
            SecondaryPortStatusSubtitle.Text = _loc["SecondaryPortDefaultDesc"];
            SecondaryPortStatusSubtitle.Foreground = TextSecondaryBrush;
            _suppressUiEvents = false;

            UpdateUiFromStatus(status);
            await RefreshProfilesAsync("Standard Landscape");
            ShowToastNotification(_loc["ToastSettingsResetTitle"], _loc["ToastSettingsResetDesc"]);
        }
        finally
        {
            ResetAllSettingsButton.IsEnabled = true;
        }
    }

    private void DevSimInjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DevSimControllerComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string presetKey)
            return;

        int basePort = _lastStatus?.ServerPort ?? 26760;
        int secPort = _lastStatus?.SecondaryPort ?? 26762;
        _automationWatcher.SimulateVirtualControllerPreset(presetKey, basePort, secPort);
        DevSimClearButton.IsEnabled = _automationWatcher.SimulatedControllerCount > 0;
        RenderControllersAndRulesUi();
    }

    private void DevSimClearButton_Click(object sender, RoutedEventArgs e)
    {
        int basePort = _lastStatus?.ServerPort ?? 26760;
        int secPort = _lastStatus?.SecondaryPort ?? 26762;
        _automationWatcher.ClearSimulatedControllers(basePort, secPort);
        DevSimClearButton.IsEnabled = false;

        IReadOnlyList<KnownControllerConfig> remaining = _automationWatcher.ScanControllers(basePort, secPort);
        int connectedCount = Math.Max(1, remaining.Count(c => c.IsConnected));
        _ = _ipcClient.SetActiveSlotAsync(connectedCount - 1);
        if (connectedCount <= 4)
        {
            _ipcClient.SetSecondaryClusterActive(false);
        }

        RenderControllersAndRulesUi();
        ShowToastNotification(_loc["ToastDevSimClearedTitle"], _loc["ToastDevSimClearedDesc"]);
    }

    private async void DevSyntheticWaveToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        DsuStatusDto status = await _ipcClient.UpdatePreferencesAsync(
            syntheticWaveEnabled: DevSyntheticWaveToggle.IsChecked == true);
        UpdateUiFromStatus(status);
    }

    private void DevLoopbackClientToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        if (DevLoopbackClientToggle.IsChecked == true)
        {
            int primaryPort = _lastStatus?.ServerPort ?? 26760;
            int secondaryPort = _lastStatus?.SecondaryPort ?? 26762;
            bool cluster = _lastStatus?.IsSecondaryClusterActive ?? false;
            _loopbackTester.Start(primaryPort, secondaryPort, cluster);
        }
        else
        {
            _loopbackTester.Stop();
        }

        DevLoopbackStatusText.Text = _loopbackTester.GetDiagnosticSummary();
    }

    private void DevTestHudButton_Click(object sender, RoutedEventArgs e)
    {
        ShowToastNotification(
            _loc["ToastDevHudSampleTitle"],
            _loc["ToastDevHudSampleDesc"],
            isError: false,
            showHudOverlay: true);
    }


    private async void UpdateChannelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded || UpdateChannelComboBox.SelectedItem is not ComboBoxItem item)
            return;

        string channel = (item.Tag as string) ?? "stable";
        DsuStatusDto status = await _ipcClient.UpdatePreferencesAsync(updateChannel: channel);
        UpdateUiFromStatus(status);
        await CheckForUpdatesInternalAsync(silentIfUpToDate: false);
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesInternalAsync(silentIfUpToDate: false);
    }

    private async Task CheckForUpdatesInternalAsync(bool silentIfUpToDate)
    {
        if (CheckUpdateButton is null || UpdateStatusText is null)
            return;

        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = _loc["UpdateStatusChecking"];
        UpdateStatusText.Foreground = TextSecondaryBrush;

        try
        {
            string channel = (_lastStatus?.UpdateChannel ?? (UpdateChannelComboBox.SelectedValue as string) ?? "stable");
            UpdateCheckResult result = await _updateService.CheckForUpdatesAsync(channel);

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                _pendingUpdateRelease = null;
                InstallUpdateButton.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = _loc.Format("UpdateStatusErrorFormat", result.ErrorMessage);
                UpdateStatusText.Foreground = StatusOfflineBrush;
                if (!silentIfUpToDate)
                {
                    ShowToastNotification(_loc["ToastUpdateCheckFailedTitle"], result.ErrorMessage, isError: true);
                }
                return;
            }

            if (result.IsUpdateAvailable && result.Release is not null)
            {
                _pendingUpdateRelease = result.Release;
                InstallUpdateButton.Visibility = Visibility.Visible;
                InstallUpdateButton.Content = _loc.Format("BtnInstallUpdateFormat", result.LatestVersion);
                UpdateStatusText.Text = _loc.Format("UpdateStatusAvailableFormat", result.LatestVersion, result.CurrentVersion);
                UpdateStatusText.Foreground = StatusOnlineBrush;

                ShowToastNotification(
                    _loc.Format("ToastUpdateAvailableTitleFormat", result.LatestVersion),
                    _loc["ToastUpdateAvailableDesc"],
                    isError: false,
                    showHudOverlay: false);
            }
            else
            {
                _pendingUpdateRelease = null;
                InstallUpdateButton.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = _loc.Format("UpdateStatusUpToDateFormat", result.CurrentVersion);
                UpdateStatusText.Foreground = TextSecondaryBrush;

                if (!silentIfUpToDate)
                {
                    ShowToastNotification(
                        _loc["ToastUpdateUpToDateTitle"],
                        _loc.Format("UpdateStatusUpToDateFormat", result.CurrentVersion));
                }
            }
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdateRelease is null)
            return;

        InstallUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;

        var progress = new Progress<int>(pct =>
        {
            UpdateStatusText.Text = _loc.Format("UpdateStatusDownloadingFormat", pct);
            UpdateStatusText.Foreground = AccentPrimaryBrush;
        });

        var (success, err) = await _updateService.DownloadAndApplyUpdateAsync(_pendingUpdateRelease, progress);
        if (!success)
        {
            InstallUpdateButton.IsEnabled = true;
            CheckUpdateButton.IsEnabled = true;
            UpdateStatusText.Text = _loc.Format("UpdateStatusErrorFormat", err ?? "Update failed");
            UpdateStatusText.Foreground = StatusOfflineBrush;
            ShowToastNotification(_loc["ToastUpdateCheckFailedTitle"], err ?? "Update failed", isError: true);
        }
    }
    private void SyncGyroMouseFromStatus(DsuStatusDto status)
    {
        if (_gyroMouseService is null || _gyroMouseService.IsListeningForTrigger)
            return;

        _gyroMouseService.IsEnabled = status.GyroMouseEnabled;
        _gyroMouseService.AlwaysActive = status.GyroMouseAlwaysActive;
        _gyroMouseService.TriggerMask = status.GyroMouseTriggerMask == 0 ? GyroMouseService.MaskLeftTrigger : status.GyroMouseTriggerMask;
        _gyroMouseService.TriggerDisplay = string.IsNullOrWhiteSpace(status.GyroMouseTriggerDisplay) ? "LT" : status.GyroMouseTriggerDisplay;
        _gyroMouseService.SpeedMultiplier = status.GyroMouseSpeed <= 0.05 ? 1.2 : status.GyroMouseSpeed;
    }

    private void UpdateGyroMouseUiControls(DsuStatusDto status)
    {
        if (DevGyroMouseToggle is null || _gyroMouseService is null)
            return;

        DevGyroMouseToggle.IsChecked = status.GyroMouseEnabled;
        if (!DevGyroMouseModeComboBox.IsDropDownOpen)
        {
            DevGyroMouseModeComboBox.SelectedIndex = status.GyroMouseAlwaysActive ? 1 : 0;
        }

        if (DevGyroMouseButtonRowGrid is not null)
        {
            DevGyroMouseButtonRowGrid.Opacity = status.GyroMouseAlwaysActive ? 0.55 : 1.0;
        }

        if (DevGyroMouseBindButton is not null)
        {
            bool listening = _gyroMouseService.IsListeningForTrigger;
            string btnDisplay = string.IsNullOrWhiteSpace(status.GyroMouseTriggerDisplay) ? "LT" : status.GyroMouseTriggerDisplay;
            DevGyroMouseBindButton.Content = listening
                ? _loc["DevGyroMouseBindListening"]
                : _loc.Format("DevGyroMouseBindBtnFormat", btnDisplay);
            DevGyroMouseBindButton.Style = (Style)FindResource(listening ? "AppPrimaryButtonStyle" : "AppSecondaryButtonStyle");
        }

        if (!DevGyroMouseSpeedSlider.IsMouseCaptureWithin)
        {
            double clampedSpeed = Math.Clamp(status.GyroMouseSpeed <= 0.05 ? 1.2 : status.GyroMouseSpeed, 0.2, 3.5);
            DevGyroMouseSpeedSlider.Value = clampedSpeed;
            if (DevGyroMouseSpeedValueText is not null)
            {
                DevGyroMouseSpeedValueText.Text = FormattableString.Invariant($"{clampedSpeed:0.0}x");
            }
        }
    }

    private async void PersistAndSyncGyroMouseSettingsAsync()
    {
        if (_gyroMouseService is null)
            return;

        DsuStatusDto status = _ipcClient.UpdateGyroMouseSettings(
            _gyroMouseService.IsEnabled,
            _gyroMouseService.AlwaysActive,
            _gyroMouseService.TriggerMask,
            _gyroMouseService.TriggerDisplay,
            (float)Math.Round(_gyroMouseService.SpeedMultiplier, 1));

        if (ProfileComboBox.SelectedItem is HandheldProfile selected)
        {
            selected.GyroMouseEnabled = status.GyroMouseEnabled;
            selected.GyroMouseAlwaysActive = status.GyroMouseAlwaysActive;
            selected.GyroMouseTriggerMask = status.GyroMouseTriggerMask;
            selected.GyroMouseTriggerDisplay = status.GyroMouseTriggerDisplay;
            selected.GyroMouseSpeed = (float)status.GyroMouseSpeed;
            status = await _ipcClient.SaveProfileAsync(selected);
            UpdateSelectedProfileDetails(selected);
        }

        UpdateUiFromStatus(status);
    }

    private void DevGyroMouseToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        bool enabled = DevGyroMouseToggle.IsChecked == true;
        _gyroMouseService.IsEnabled = enabled;
        PersistAndSyncGyroMouseSettingsAsync();
        ShowToastNotification(
            enabled ? _loc["ToastDevGyroMouseOnTitle"] : _loc["ToastDevGyroMouseOffTitle"],
            enabled ? _loc["ToastDevGyroMouseOnDesc"] : _loc["ToastDevGyroMouseOffDesc"],
            isError: false,
            showHudOverlay: true);
    }

    private void DevGyroMouseModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _gyroMouseService is null || DevGyroMouseModeComboBox.SelectedItem is not ComboBoxItem item)
            return;

        string tag = (item.Tag as string) ?? "hold";
        _gyroMouseService.AlwaysActive = string.Equals(tag, "always", StringComparison.OrdinalIgnoreCase);
        PersistAndSyncGyroMouseSettingsAsync();
    }

    private void DevGyroMouseBindButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gyroMouseService is null)
            return;

        if (_gyroMouseService.IsListeningForTrigger)
        {
            _gyroMouseService.CancelListeningForTrigger();
        }
        else
        {
            _gyroMouseService.StartListeningForTrigger();
        }

        if (_lastStatus is not null)
        {
            _suppressUiEvents = true;
            UpdateGyroMouseUiControls(_lastStatus);
            _suppressUiEvents = false;
        }
    }

    private void OnGyroMouseTriggerButtonBound(object? sender, (uint Mask, string Display) e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            PersistAndSyncGyroMouseSettingsAsync();
            ShowToastNotification(
                _loc["ToastGyroMouseButtonBoundTitle"],
                _loc.Format("ToastGyroMouseButtonBoundDescFormat", e.Display),
                isError: false,
                showHudOverlay: true);
        });
    }

    private void RefreshRpcs3PatchStatusUi()
    {
        if (DevRpcs3PathText is null || DevRpcs3StatusBadgeText is null)
            return;

        (Rpcs3PatchState state, string? exePath, bool hasBackup) = Rpcs3SdlPatcherService.GetCurrentStatus();

        switch (state)
        {
            case Rpcs3PatchState.Patched:
                DevRpcs3StatusBadgeText.Text = _loc["DevRpcs3BadgePatched"];
                DevRpcs3StatusBadgeText.Foreground = StatusOnlineBrush;
                DevRpcs3PathText.Text = exePath ?? string.Empty;
                DevRpcs3ApplyPatchButton.IsEnabled = true;
                DevRpcs3RestoreButton.IsEnabled = true;
                break;

            case Rpcs3PatchState.Unpatched:
                DevRpcs3StatusBadgeText.Text = _loc["DevRpcs3BadgeUnpatched"];
                DevRpcs3StatusBadgeText.Foreground = AccentPrimaryBrush;
                DevRpcs3PathText.Text = exePath ?? string.Empty;
                DevRpcs3ApplyPatchButton.IsEnabled = true;
                DevRpcs3RestoreButton.IsEnabled = hasBackup;
                break;

            case Rpcs3PatchState.UnsupportedBinary:
                DevRpcs3StatusBadgeText.Text = _loc["DevRpcs3BadgeUnsupported"];
                DevRpcs3StatusBadgeText.Foreground = StatusOfflineBrush;
                DevRpcs3PathText.Text = exePath ?? string.Empty;
                DevRpcs3ApplyPatchButton.IsEnabled = false;
                DevRpcs3RestoreButton.IsEnabled = hasBackup;
                break;

            default:
                DevRpcs3StatusBadgeText.Text = _loc["DevRpcs3BadgeNotFound"];
                DevRpcs3StatusBadgeText.Foreground = TextSecondaryBrush;
                DevRpcs3PathText.Text = _loc["DevRpcs3NotFoundDesc"];
                DevRpcs3ApplyPatchButton.IsEnabled = true;
                DevRpcs3RestoreButton.IsEnabled = false;
                break;
        }
    }

    private void DevRpcs3ApplyPatchButton_Click(object sender, RoutedEventArgs e)
    {
        string? exePath = Rpcs3SdlPatcherService.DiscoverRpcs3ExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            exePath = PromptForRpcs3ExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath))
                return;
        }

        if (Rpcs3SdlPatcherService.ApplyPatch(exePath, out string errorMsg))
        {
            RefreshRpcs3PatchStatusUi();
            ShowToastNotification(
                _loc["ToastRpcs3PatchedTitle"],
                _loc["ToastRpcs3PatchedDesc"],
                isError: false,
                showHudOverlay: true);
        }
        else
        {
            RefreshRpcs3PatchStatusUi();
            ShowToastNotification(
                _loc["ToastRpcs3PatchErrorTitle"],
                errorMsg,
                isError: true,
                showHudOverlay: true);
        }
    }

    private void DevRpcs3RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        string? exePath = Rpcs3SdlPatcherService.DiscoverRpcs3ExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath))
            return;

        if (Rpcs3SdlPatcherService.RestoreOriginal(exePath, out string errorMsg))
        {
            RefreshRpcs3PatchStatusUi();
            ShowToastNotification(
                _loc["ToastRpcs3RestoredTitle"],
                _loc["ToastRpcs3RestoredDesc"],
                isError: false,
                showHudOverlay: true);
        }
        else
        {
            RefreshRpcs3PatchStatusUi();
            ShowToastNotification(
                _loc["ToastRpcs3PatchErrorTitle"],
                errorMsg,
                isError: true,
                showHudOverlay: true);
        }
    }

    private void DevRpcs3BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        string? picked = PromptForRpcs3ExecutablePath();
        if (!string.IsNullOrWhiteSpace(picked))
        {
            Rpcs3SdlPatcherService.CustomExePath = picked;
            RefreshRpcs3PatchStatusUi();
        }
    }

    private static string? PromptForRpcs3ExecutablePath()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select rpcs3.exe",
            Filter = "RPCS3 Executable (rpcs3.exe)|rpcs3.exe|All Executables (*.exe)|*.exe",
            CheckFileExists = true
        };

        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void DevGyroMouseSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DevGyroMouseSpeedValueText is not null)
        {
            DevGyroMouseSpeedValueText.Text = FormattableString.Invariant($"{e.NewValue:0.0}x");
        }

        if (_gyroMouseService is not null)
        {
            _gyroMouseService.SpeedMultiplier = e.NewValue;
            if (!_suppressUiEvents && IsLoaded)
            {
                PersistAndSyncGyroMouseSettingsAsync();
            }

        }
    }
}
