using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using HandheldDsuServer.Models;
using HandheldDsuWidget.Services;

namespace HandheldDsuWidget;

public partial class MainWindow
{
    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        bool isWindowHiddenOrMinimized = !IsVisible || WindowState == WindowState.Minimized;

        _gameBarAdapter.PollGameBarCompanionState(
            isEnabled: false,
            isWindowVisible: !isWindowHiddenOrMinimized,
            isOurWindowActive: IsVisible && (IsActive || IsMouseOver),
            isPinnedByUser: _isPinnedByUser);

        if (isWindowHiddenOrMinimized)
        {
            // Battery/CPU optimization: when hidden in the system tray, skip 22 FPS WPF visual updates
            // and only refresh cached status once per ~1.1s for tray context menu readiness.
            _backgroundIdleTickCounter++;
            if (_backgroundIdleTickCounter < 25)
                return;
            _backgroundIdleTickCounter = 0;
        }
        else
        {
            _backgroundIdleTickCounter = 0;
        }

        if (_isPollBusy)
            return;

        _isPollBusy = true;
        try
        {
            DsuStatusDto status = await _ipcClient.GetStatusAsync();
            if (isWindowHiddenOrMinimized)
            {
                _lastStatus = status;
                SyncGyroMouseFromStatus(status);
            }
            else
            {
                UpdateUiFromStatus(status);
            }
        }
        catch
        {
            // Ignore transient status polling errors
        }
        finally
        {
            _isPollBusy = false;
        }
    }

    private void UpdateAxisBadgeVisual(Border badgeBorder, TextBlock badgeText, bool isActive)
    {
        badgeBorder.Background = isActive ? AccentDimBrush : BgSubPanelBrush;
        badgeBorder.BorderBrush = isActive ? AccentPrimaryBrush : BorderInputBrush;
        badgeText.Foreground = isActive ? TextOnAccentBrush : TextSecondaryBrush;
    }

    private void UpdateUiFromStatus(DsuStatusDto status)
    {
        _lastStatus = status;
        _suppressUiEvents = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(status.Language))
            {
                _loc.SetLanguage(status.Language);
                if (!LanguageComboBox.IsDropDownOpen)
                {
                    LanguageComboBox.SelectedValue = _loc.CurrentLanguage;
                }
            }

            EngineSubtitleText.Text = _loc.Format("HeaderSubtitleFormat", status.ServerPort, status.IpcPort);
            CopyEndpointButton.Content = FormattableString.Invariant($"127.0.0.1:{status.ServerPort} ⧉");
            MultiPortClusterToggle.IsChecked = status.IsSecondaryClusterActive;
            SecondaryPortRowGrid.Visibility = status.IsSecondaryClusterActive ? Visibility.Visible : Visibility.Collapsed;

            if (status.WasPortFallbackTriggered && PortStatusSubtitle.Text == _loc["PortDefaultDesc"])
            {
                PortStatusSubtitle.Text = _loc.Format("PortFallbackWarningFormat", status.ServerPort);
                PortStatusSubtitle.Foreground = AccentPrimaryBrush;
            }

            if (!string.IsNullOrWhiteSpace(status.ActiveProfileName) && !ProfileComboBox.IsDropDownOpen)
            {
                if ((string?)ProfileComboBox.SelectedValue != status.ActiveProfileName)
                {
                    ProfileComboBox.SelectedValue = status.ActiveProfileName;
                    var activeMatch = _profiles.FirstOrDefault(p => string.Equals(p.Name, status.ActiveProfileName, StringComparison.OrdinalIgnoreCase));
                    if (activeMatch is not null)
                    {
                        UpdateSelectedProfileDetails(activeMatch);
                    }
                }
            }

            // Only sync the port input fields when the backend port value actually changed
            // (prevents 45ms poll ticks from overwriting user edits when clicking the APPLY button)
            if (_lastSyncedServerPort != status.ServerPort && !PortTextBox.IsKeyboardFocusWithin)
            {
                _lastSyncedServerPort = status.ServerPort;
                PortTextBox.Text = FormattableString.Invariant($"{status.ServerPort}");
            }

            if (_lastSyncedSecondaryPort != status.SecondaryPort && !SecondaryPortTextBox.IsKeyboardFocusWithin)
            {
                _lastSyncedSecondaryPort = status.SecondaryPort;
                SecondaryPortTextBox.Text = FormattableString.Invariant($"{status.SecondaryPort}");
            }

            // 1. Emulator connection status indicator
            if (status.IsEmulatorConnected)
            {
                StatusBadgeBorder.Background = StatusOnlineDimBrush;
                StatusBadgeBorder.BorderBrush = StatusOnlineBrush;
                StatusSignalIcon.Stroke = StatusOnlineBrush;
                ConnectionStatusTitle.Text = _loc["StatusConnected"];
                PacketCounterText.Text = _loc.Format("StatusPacketsFormat", status.TotalPacketsSent);
            }
            else
            {
                StatusBadgeBorder.Background = StatusOfflineDimBrush;
                StatusBadgeBorder.BorderBrush = StatusOfflineBrush;
                StatusSignalIcon.Stroke = StatusOfflineBrush;
                ConnectionStatusTitle.Text = _loc["StatusWaiting"];
                PacketCounterText.Text = status.TotalPacketsSent > 0
                    ? _loc.Format("StatusPacketsFormat", status.TotalPacketsSent)
                    : _loc["StatusReadyBadge"];
            }

            // 2. DSU stream, Full Gamepad Forwarding, and Start with Windows switches
            GyroStreamToggle.IsChecked = status.IsStreamingEnabled;
            GyroToggleSubtitle.Text = status.IsStreamingEnabled
                ? _loc["StreamActiveDesc"]
                : _loc["StreamPausedDesc"];

            ForwardGamepadToggle.IsChecked = status.ForwardGamepadInputs;
            ForwardGamepadSubtitle.Text = status.ForwardGamepadInputs
                ? _loc["ForwardGamepadActiveDesc"]
                : _loc["ForwardGamepadDisabledDesc"];

            StartupToggle.IsChecked = status.StartWithWindows;
            StartupToggleSubtitle.Text = status.StartWithWindows
                ? _loc["StartupEnabledDesc"]
                : _loc["StartupDisabledDesc"];

            CloseToTrayToggle.IsChecked = status.CloseToTray;
            GameBarOverlayToggle.IsChecked = status.GameBarOverlayEnabled;
            HudNotificationsToggle.IsChecked = status.HudNotificationsEnabled;
            TabletopCalibrationGuardToggle.IsChecked = status.TabletopCalibrationGuard;
            DevSyntheticWaveToggle.IsChecked = status.IsSyntheticWaveEnabled;
            DevLoopbackClientToggle.IsChecked = _loopbackTester.IsRunning;
            _loopbackTester.UpdateTargetPorts(status.ServerPort, status.SecondaryPort, status.IsSecondaryClusterActive);
            DevLoopbackStatusText.Text = _loopbackTester.GetDiagnosticSummary();
            DevSimClearButton.IsEnabled = _automationWatcher.SimulatedControllerCount > 0;
            SyncGyroMouseFromStatus(status);
            UpdateGyroMouseUiControls(status);

            // 3. Axis orientation switches, sensitivity/deadzone sliders, and active badge highlights
            PitchOffsetToggle.IsChecked = status.UsePitchOffset;
            InvertXToggle.IsChecked = status.InvertX;
            InvertYToggle.IsChecked = status.InvertY;
            InvertZToggle.IsChecked = status.InvertZ;
            SwapAxesToggle.IsChecked = status.SwapAxes;

            if (!SensitivitySlider.IsMouseCaptureWithin)
            {
                SensitivitySlider.Value = Math.Clamp(status.Sensitivity, 0.2f, 3.0f);
            }
            SensitivityValueText.Text = FormattableString.Invariant($"{status.Sensitivity:0.0}x");

            if (!DeadzoneSlider.IsMouseCaptureWithin)
            {
                DeadzoneSlider.Value = Math.Clamp(status.Deadzone, 0.0f, 3.0f);
            }
            DeadzoneValueText.Text = FormattableString.Invariant($"{status.Deadzone:0.0}°/s");

            UpdateAxisBadgeVisual(BadgePitchOffset, BadgePitchOffsetText, status.UsePitchOffset);
            UpdateAxisBadgeVisual(BadgeInvertX, BadgeInvertXText, status.InvertX);
            UpdateAxisBadgeVisual(BadgeInvertY, BadgeInvertYText, status.InvertY);
            UpdateAxisBadgeVisual(BadgeInvertZ, BadgeInvertZText, status.InvertZ);
            UpdateAxisBadgeVisual(BadgeSwapAxes, BadgeSwapAxesText, status.SwapAxes);
            UpdateAxisBadgeVisual(BadgeSensitivity, BadgeSensitivityText, Math.Abs(status.Sensitivity - 1.0f) > 0.04f);
            UpdateAxisBadgeVisual(BadgeDeadzone, BadgeDeadzoneText, status.Deadzone > 0.04f);

            UpdateProfileIndicatorUi(ProfileComboBox.SelectedItem as HandheldProfile, status);

            // 4. Live IMU values (deg/s & G) and Live Gyro Crosshair / Horizon Radar Scope
            PitchValueText.Text = FormattableString.Invariant($"{status.GyroPitch:+0.00;-0.00;0.00}°/s");
            YawValueText.Text = FormattableString.Invariant($"{status.GyroYaw:+0.00;-0.00;0.00}°/s");
            RollValueText.Text = FormattableString.Invariant($"{status.GyroRoll:+0.00;-0.00;0.00}°/s");

            if (status.IsStreamingEnabled)
            {
                double targetX = Math.Clamp((-status.GyroYaw * 0.45) + (status.GyroRoll * 0.18), -22.0, 22.0);
                double targetY = Math.Clamp(-status.GyroPitch * 0.45, -20.0, 20.0);
                _radarX = (_radarX * 0.68) + (targetX * 0.32);
                _radarY = (_radarY * 0.68) + (targetY * 0.32);

                double gravityYZ = Math.Max(0.35, Math.Sqrt((status.AccY * status.AccY) + (status.AccZ * status.AccZ)));
                double accelRollDeg = Math.Atan2(status.AccX, gravityYZ) * (180.0 / Math.PI);
                if (Math.Abs(accelRollDeg) < 2.0 && Math.Abs(status.GyroRoll) < 1.5)
                {
                    accelRollDeg = 0.0;
                }

                double targetRoll = Math.Clamp(accelRollDeg + (status.GyroRoll * 0.22), -45.0, 45.0);
                _radarRoll = (_radarRoll * 0.72) + (targetRoll * 0.28);
            }
            else
            {
                _radarX *= 0.6;
                _radarY *= 0.6;
                _radarRoll *= 0.6;
            }

            RadarDotTranslate.X = _radarX;
            RadarDotTranslate.Y = _radarY;
            RadarRollRotate.Angle = _radarRoll;

            BiasStatusText.Text = _loc.Format(
                "TelemetryFooterFormat",
                status.AccX,
                status.AccY,
                status.AccZ,
                status.BiasX,
                status.BiasY,
                status.BiasZ);

            // 5. Calibration button state
            if (status.IsCalibrating)
            {
                CalibrateButton.IsEnabled = false;
                CalibrateButtonText.Text = _loc.Format("CalibratingFormat", status.CalibrationProgress);
            }
            else
            {
                CalibrateButton.IsEnabled = true;
                CalibrateButtonText.Text = _loc["CalibrateButton"];
            }
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    public async Task TriggerCalibrationFromControllerAsync()
    {
        if (!CalibrateButton.IsEnabled)
            return;

        await RunCalibrationUiFlowAsync();
    }

    private async void CalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCalibrationUiFlowAsync();
    }

    private async Task RunCalibrationUiFlowAsync()
    {
        CalibrateButton.IsEnabled = false;
        CalibrateButtonText.Text = _loc.Format("CalibratingFormat", 0);

        try
        {
            DsuStatusDto status = await _ipcClient.CalibrateAsync();
            UpdateUiFromStatus(status);

            await Task.Delay(1100);
            DsuStatusDto finalStatus = await _ipcClient.GetStatusAsync();
            UpdateUiFromStatus(finalStatus);
        }
        finally
        {
            if (!CalibrateButton.IsEnabled)
            {
                CalibrateButton.IsEnabled = true;
                CalibrateButtonText.Text = _loc["CalibrateButton"];
            }
        }
    }

    private void CopyEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        int port = _lastStatus?.ServerPort ?? 26760;
        string endpoint = FormattableString.Invariant($"127.0.0.1:{port}");
        try
        {
            Clipboard.SetText(endpoint);
            ShowToastNotification(
                _loc.Format("ToastEndpointCopiedTitleFormat", endpoint),
                _loc["ToastEndpointCopiedDesc"]);
        }
        catch
        {
            // Ignore clipboard access contention
        }
    }

    private async void GyroStreamToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        bool targetState = GyroStreamToggle.IsChecked == true;
        DsuStatusDto status = await _ipcClient.ToggleStreamingAsync(targetState);
        UpdateUiFromStatus(status);
    }

    private async void ForwardGamepadToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded)
            return;

        bool targetState = ForwardGamepadToggle.IsChecked == true;
        DsuStatusDto status = await _ipcClient.UpdatePreferencesAsync(forwardGamepadInputs: targetState);
        UpdateUiFromStatus(status);
    }

    private async void AxisConfigToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded)
            return;

        DsuStatusDto status = await _ipcClient.UpdateAxesAsync(
            invertX: InvertXToggle.IsChecked == true,
            invertY: InvertYToggle.IsChecked == true,
            invertZ: InvertZToggle.IsChecked == true,
            swapAxes: SwapAxesToggle.IsChecked == true,
            usePitchOffset: PitchOffsetToggle.IsChecked == true,
            sensitivity: (float)Math.Round(SensitivitySlider.Value, 1),
            deadzone: (float)Math.Round(DeadzoneSlider.Value, 1));

        await SyncActiveProfileAfterAxisChangeAsync(status);
    }

    private async void AxisSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || !IsLoaded)
            return;

        float sensitivity = (float)Math.Round(SensitivitySlider.Value, 1);
        float deadzone = (float)Math.Round(DeadzoneSlider.Value, 1);
        SensitivityValueText.Text = FormattableString.Invariant($"{sensitivity:0.0}x");
        DeadzoneValueText.Text = FormattableString.Invariant($"{deadzone:0.0}°/s");

        DsuStatusDto status = await _ipcClient.UpdateAxesAsync(
            invertX: InvertXToggle.IsChecked == true,
            invertY: InvertYToggle.IsChecked == true,
            invertZ: InvertZToggle.IsChecked == true,
            swapAxes: SwapAxesToggle.IsChecked == true,
            usePitchOffset: PitchOffsetToggle.IsChecked == true,
            sensitivity: sensitivity,
            deadzone: deadzone);

        await SyncActiveProfileAfterAxisChangeAsync(status);
    }

    private async void ResetAxesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded)
            return;

        DsuStatusDto status = await _ipcClient.ResetAxesAsync();
        await SyncActiveProfileAfterAxisChangeAsync(status);
    }

    private async Task SyncActiveProfileAfterAxisChangeAsync(DsuStatusDto status)
    {
        if (ProfileComboBox.SelectedItem is HandheldProfile selected && !IsSystemPreset(selected))
        {
            selected.InvertX = status.InvertX;
            selected.InvertY = status.InvertY;
            selected.InvertZ = status.InvertZ;
            selected.SwapAxes = status.SwapAxes;
            selected.UsePitchOffset = status.UsePitchOffset;
            selected.Sensitivity = (float)status.Sensitivity;
            selected.Deadzone = (float)status.Deadzone;
            status = await _ipcClient.SaveProfileAsync(selected);
            UpdateSelectedProfileDetails(selected);
        }

        UpdateUiFromStatus(status);
    }
}
