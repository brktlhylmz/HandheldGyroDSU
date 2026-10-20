using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using HandheldDsuServer.Models;
using HandheldDsuServer.Services;
using HandheldDsuWidget.Services;

namespace HandheldDsuWidget;

public partial class MainWindow : Window
{
    private readonly DsuIpcClient _ipcClient = new();
    private readonly GameBarWidgetAdapter _gameBarAdapter = new();
    private readonly SystemTrayService _trayService = new();
    private readonly GamepadNavigationService _gamepadService;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly DispatcherTimer _pollTimer;
    private readonly bool _startedMinimized;

    private List<HandheldProfile> _profiles = [];
    private DispatcherTimer? _profileFeedbackTimer;
    private bool _suppressUiEvents;
    private bool _isPollBusy;
    private DsuStatusDto? _lastStatus;

    private Brush StatusOnlineBrush => (Brush)FindResource("BrushStatusOnline");
    private Brush StatusOnlineDimBrush => (Brush)FindResource("BrushStatusOnlineDim");
    private Brush StatusOfflineBrush => (Brush)FindResource("BrushStatusOffline");
    private Brush StatusOfflineDimBrush => (Brush)FindResource("BrushStatusOfflineDim");
    private Brush TextSecondaryBrush => (Brush)FindResource("BrushTextSecondary");
    private Brush TextOnAccentBrush => (Brush)FindResource("BrushTextOnAccent");
    private Brush AccentPrimaryBrush => (Brush)FindResource("BrushAccentPrimary");
    private Brush AccentDimBrush => (Brush)FindResource("BrushAccentDim");
    private Brush BgSubPanelBrush => (Brush)FindResource("BrushBgSubPanel");
    private Brush BorderInputBrush => (Brush)FindResource("BrushBorderInput");

    public MainWindow() : this(startMinimized: false)
    {
    }

    public MainWindow(bool startMinimized)
    {
        _startedMinimized = startMinimized;
        InitializeComponent();

        _gamepadService = new GamepadNavigationService(this);

        LanguageComboBox.ItemsSource = _loc.AvailableLanguages;
        LanguageComboBox.SelectedValue = _loc.CurrentLanguage;

        _loc.LanguageChanged += (_, _) =>
        {
            _trayService.UpdateTooltip(_loc["TrayTooltip"]);
            PortStatusSubtitle.Text = _loc["PortDefaultDesc"];
            PortStatusSubtitle.Foreground = TextSecondaryBrush;
            if (_lastStatus is not null)
            {
                UpdateUiFromStatus(_lastStatus);
            }
        };

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _pollTimer.Tick += PollTimer_Tick;

        SourceInitialized += (_, _) =>
        {
            _trayService.Initialize(this, _loc["TrayTooltip"]);
            _trayService.TrayLeftClicked += (_, _) => ToggleWindowVisibility();
            _trayService.TrayRightClicked += (_, _) => ShowFromTray();
        };

        Loaded += async (_, _) =>
        {
            DsuStatusDto status = await _ipcClient.GetStatusAsync();
            _loc.SetLanguage(status.Language);
            UpdateUiFromStatus(status);
            await RefreshProfilesAsync(status.ActiveProfileName);
            _pollTimer.Start();
            _gamepadService.Start();
        };

        Closed += (_, _) =>
        {
            _profileFeedbackTimer?.Stop();
            _gamepadService.Dispose();
            _pollTimer.Stop();
            _trayService.Dispose();
            _ipcClient.Dispose();
        };
    }

    public IReadOnlyList<Control> GetActiveTabNavigableControls()
    {
        if (SettingsTabButton.IsChecked == true)
        {
            return
            [
                LanguageComboBox,
                ProfileComboBox,
                DeleteProfileButton,
                NewProfileNameTextBox,
                SaveProfileButton,
                ImportProfileButton,
                ExportProfileButton,
                PortTextBox,
                ApplyPortButton,
                StartupToggle,
                CalibrateButton
            ];
        }

        return
        [
            GyroStreamToggle,
            PitchOffsetToggle,
            InvertXToggle,
            InvertYToggle,
            InvertZToggle,
            SwapAxesToggle,
            ResetAxesButton,
            CalibrateButton
        ];
    }

    public void ScrollByVerticalOffset(double delta)
    {
        if (MainScrollViewer is null)
            return;

        double target = Math.Clamp(MainScrollViewer.VerticalOffset + delta, 0, MainScrollViewer.ScrollableHeight);
        MainScrollViewer.ScrollToVerticalOffset(target);
    }

    public void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Focus();
    }

    private void ToggleWindowVisibility()
    {
        if (IsVisible)
        {
            HideToTray();
        }
        else
        {
            ShowFromTray();
        }
    }

    public void SwitchTab(bool showSettings)
    {
        if (showSettings)
        {
            SettingsTabButton.IsChecked = true;
        }
        else
        {
            DashboardTabButton.IsChecked = true;
        }
    }

    private void NavTab_Checked(object sender, RoutedEventArgs e)
    {
        if (DashboardViewPanel is null || SettingsViewPanel is null)
            return;

        bool isSettings = SettingsTabButton.IsChecked == true;
        DashboardViewPanel.Visibility = isSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsViewPanel.Visibility = isSettings ? Visibility.Visible : Visibility.Collapsed;
        MainScrollViewer?.ScrollToTop();
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_isPollBusy)
            return;

        _isPollBusy = true;
        try
        {
            DsuStatusDto status = await _ipcClient.GetStatusAsync();
            UpdateUiFromStatus(status);
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

            // Update the port input field unless the user is actively typing in it
            if (!PortTextBox.IsKeyboardFocusWithin)
            {
                PortTextBox.Text = status.ServerPort.ToString();
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

            // 2. DSU stream and Start with Windows switches
            GyroStreamToggle.IsChecked = status.IsStreamingEnabled;
            GyroToggleSubtitle.Text = status.IsStreamingEnabled
                ? _loc["StreamActiveDesc"]
                : _loc["StreamPausedDesc"];

            StartupToggle.IsChecked = status.StartWithWindows;
            StartupToggleSubtitle.Text = status.StartWithWindows
                ? _loc["StartupEnabledDesc"]
                : _loc["StartupDisabledDesc"];

            // 3. Axis orientation switches and active badge highlights
            PitchOffsetToggle.IsChecked = status.UsePitchOffset;
            InvertXToggle.IsChecked = status.InvertX;
            InvertYToggle.IsChecked = status.InvertY;
            InvertZToggle.IsChecked = status.InvertZ;
            SwapAxesToggle.IsChecked = status.SwapAxes;

            UpdateAxisBadgeVisual(BadgePitchOffset, BadgePitchOffsetText, status.UsePitchOffset);
            UpdateAxisBadgeVisual(BadgeInvertX, BadgeInvertXText, status.InvertX);
            UpdateAxisBadgeVisual(BadgeInvertY, BadgeInvertYText, status.InvertY);
            UpdateAxisBadgeVisual(BadgeInvertZ, BadgeInvertZText, status.InvertZ);
            UpdateAxisBadgeVisual(BadgeSwapAxes, BadgeSwapAxesText, status.SwapAxes);

            // 4. Live IMU values (deg/s & G)
            PitchValueText.Text = $"{status.GyroPitch:+0.00;-0.00;0.00}°/s";
            YawValueText.Text = $"{status.GyroYaw:+0.00;-0.00;0.00}°/s";
            RollValueText.Text = $"{status.GyroRoll:+0.00;-0.00;0.00}°/s";

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

    private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded)
            return;

        if (LanguageComboBox.SelectedValue is string langCode)
        {
            _loc.SetLanguage(langCode);
            DsuStatusDto status = await _ipcClient.SetLanguageAsync(langCode);
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
                PortStatusSubtitle.Text = _loc.Format("PortSavedFormat", newPort);
                PortStatusSubtitle.Foreground = StatusOnlineBrush;
            }
            else
            {
                PortStatusSubtitle.Text = status.Message;
                PortStatusSubtitle.Foreground = StatusOfflineBrush;
            }
            UpdateUiFromStatus(status);
        }
        finally
        {
            ApplyPortButton.IsEnabled = true;
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

    private async void GyroStreamToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        bool targetState = GyroStreamToggle.IsChecked == true;
        DsuStatusDto status = await _ipcClient.ToggleStreamingAsync(targetState);
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
            usePitchOffset: PitchOffsetToggle.IsChecked == true);
        UpdateUiFromStatus(status);
    }

    private async void ResetAxesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded)
            return;

        DsuStatusDto status = await _ipcClient.ResetAxesAsync();
        UpdateUiFromStatus(status);
    }

    private static bool IsSystemPreset(HandheldProfile profile)
    {
        return string.Equals(profile.Author, "System", StringComparison.OrdinalIgnoreCase)
            || string.Equals(profile.Name, "Standard Landscape", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateSelectedProfileDetails(HandheldProfile profile)
    {
        var tags = new List<string>();
        if (profile.UsePitchOffset) tags.Add("0° PITCH");
        if (profile.InvertX) tags.Add("INV-X");
        if (profile.InvertY) tags.Add("INV-Y");
        if (profile.InvertZ) tags.Add("INV-Z");
        if (profile.SwapAxes) tags.Add("SWAP X⇄Y");

        ProfileDescriptionText.Text = tags.Count > 0
            ? string.Join("  •  ", tags)
            : "RAW (NO OFFSET / NO INVERSION)";

        DeleteProfileButton.IsEnabled = !IsSystemPreset(profile);
    }

    private void ShowProfileStatus(string message, bool isError = false)
    {
        ProfileStatusFeedbackText.Text = message;
        ProfileStatusFeedbackText.Foreground = isError ? StatusOfflineBrush : StatusOnlineBrush;
        ProfileStatusFeedbackText.Visibility = Visibility.Visible;

        _profileFeedbackTimer?.Stop();
        _profileFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _profileFeedbackTimer.Tick += (_, _) =>
        {
            _profileFeedbackTimer?.Stop();
            ProfileStatusFeedbackText.Visibility = Visibility.Collapsed;
        };
        _profileFeedbackTimer.Start();
    }

    private async Task RefreshProfilesAsync(string? selectProfileName = null)
    {
        _suppressUiEvents = true;
        try
        {
            IReadOnlyList<HandheldProfile> loaded = await _ipcClient.GetProfilesAsync();
            _profiles = loaded.ToList();
            ProfileComboBox.ItemsSource = null;
            ProfileComboBox.ItemsSource = _profiles;

            string target = selectProfileName ?? _lastStatus?.ActiveProfileName ?? _profiles.FirstOrDefault()?.Name ?? "Standard Landscape";
            var match = _profiles.FirstOrDefault(p => string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase))
                        ?? _profiles.FirstOrDefault();

            if (match is not null)
            {
                ProfileComboBox.SelectedValue = match.Name;
                UpdateSelectedProfileDetails(match);
            }
        }
        catch
        {
            // Ignore directory read error
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private async void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileComboBox.SelectedItem is not HandheldProfile selected)
            return;

        UpdateSelectedProfileDetails(selected);

        if (_suppressUiEvents || !IsLoaded)
            return;

        DsuStatusDto status = await _ipcClient.ApplyProfileAsync(selected.Name);
        UpdateUiFromStatus(status);
        ShowProfileStatus(_loc.Format("ProfileAppliedFormat", selected.Name));
    }

    private async void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileComboBox.SelectedItem is not HandheldProfile profile || IsSystemPreset(profile))
            return;

        DeleteProfileButton.IsEnabled = false;
        try
        {
            DsuStatusDto status = await _ipcClient.DeleteProfileAsync(profile.Name);
            UpdateUiFromStatus(status);
            await RefreshProfilesAsync("Standard Landscape");
            ShowProfileStatus(_loc.Format("ProfileDeleteSuccess", profile.Name));
        }
        finally
        {
            if (ProfileComboBox.SelectedItem is HandheldProfile current)
            {
                DeleteProfileButton.IsEnabled = !IsSystemPreset(current);
            }
        }
    }

    private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrentProfileAsync();
    }

    private async void NewProfileNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SaveCurrentProfileAsync();
            Keyboard.ClearFocus();
        }
    }

    private async Task SaveCurrentProfileAsync()
    {
        string profileName = NewProfileNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(profileName))
            return;

        var profile = new HandheldProfile
        {
            Name = profileName,
            Author = Environment.UserName,
            Description = $"Custom user profile ({DateTime.Now:yyyy-MM-dd})",
            InvertX = InvertXToggle.IsChecked == true,
            InvertY = InvertYToggle.IsChecked == true,
            InvertZ = InvertZToggle.IsChecked == true,
            SwapAxes = SwapAxesToggle.IsChecked == true,
            UsePitchOffset = PitchOffsetToggle.IsChecked == true
        };

        SaveProfileButton.IsEnabled = false;
        try
        {
            DsuStatusDto status = await _ipcClient.SaveProfileAsync(profile);
            NewProfileNameTextBox.Text = string.Empty;
            UpdateUiFromStatus(status);
            await RefreshProfilesAsync(profile.Name);
            ShowProfileStatus(_loc.Format("ProfileSaveSuccess", profile.Name));
        }
        finally
        {
            SaveProfileButton.IsEnabled = true;
        }
    }

    private void ExportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        HandheldProfile? profile = ProfileComboBox.SelectedItem as HandheldProfile;
        if (profile is null)
        {
            profile = new HandheldProfile
            {
                Name = "Custom Profile",
                InvertX = InvertXToggle.IsChecked == true,
                InvertY = InvertYToggle.IsChecked == true,
                InvertZ = InvertZToggle.IsChecked == true,
                SwapAxes = SwapAxesToggle.IsChecked == true,
                UsePitchOffset = PitchOffsetToggle.IsChecked == true
            };
        }

        var sfd = new SaveFileDialog
        {
            Title = _loc["BtnExportFile"],
            Filter = "JSON Profile (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"{profile.Name}.json"
        };

        if (sfd.ShowDialog(this) == true)
        {
            try
            {
                string json = ProfileManager.SerializeProfile(profile);
                File.WriteAllText(sfd.FileName, json);
                ShowProfileStatus(_loc.Format("ProfileExportSuccess", Path.GetFileName(sfd.FileName)));
            }
            catch (Exception ex)
            {
                ShowProfileStatus($"Export failed: {ex.Message}", isError: true);
            }
        }
    }

    private async void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = _loc["BtnImportFile"],
            Filter = "JSON Profile (*.json)|*.json|All files (*.*)|*.*",
            Multiselect = false
        };

        if (ofd.ShowDialog(this) == true)
        {
            try
            {
                string json = File.ReadAllText(ofd.FileName);
                HandheldProfile? imported = ProfileManager.DeserializeProfile(json);
                if (imported is null)
                {
                    ShowProfileStatus(_loc["ProfileInvalidError"], isError: true);
                    return;
                }

                DsuStatusDto status = await _ipcClient.SaveProfileAsync(imported);
                await _ipcClient.ApplyProfileAsync(imported.Name);
                UpdateUiFromStatus(status);
                await RefreshProfilesAsync(imported.Name);
                ShowProfileStatus(_loc.Format("ProfileImportSuccess", imported.Name));
            }
            catch (Exception ex)
            {
                ShowProfileStatus($"Import failed: {ex.Message}", isError: true);
            }
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinButton.Foreground = Topmost ? StatusOnlineBrush : TextSecondaryBrush;
    }

    private void MinimizeToTrayButton_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
