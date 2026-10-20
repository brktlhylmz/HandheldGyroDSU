using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private readonly HudToastService _hudToastService = new();
    private readonly ControllerAndAppWatcherService _automationWatcher = new();
    private readonly GamepadNavigationService _gamepadService;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly DispatcherTimer _pollTimer;
    private readonly bool _startedMinimized;

    private List<HandheldProfile> _profiles = [];
    private DispatcherTimer? _snackbarTimer;
    private ContextMenu? _trayContextMenu;
    private bool _suppressUiEvents;
    private bool _isPollBusy;
    private DsuStatusDto? _lastStatus;
    private double _radarX;
    private double _radarY;
    private double _radarRoll;

    private Brush StatusOnlineBrush => (Brush)FindResource("BrushStatusOnline");
    private Brush StatusOnlineDimBrush => (Brush)FindResource("BrushStatusOnlineDim");
    private Brush StatusOfflineBrush => (Brush)FindResource("BrushStatusOffline");
    private Brush StatusOfflineDimBrush => (Brush)FindResource("BrushStatusOfflineDim");
    private Brush TextPrimaryBrush => (Brush)FindResource("BrushTextPrimary");
    private Brush TextSecondaryBrush => (Brush)FindResource("BrushTextSecondary");
    private Brush TextMutedBrush => (Brush)FindResource("BrushTextMuted");
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

        _automationWatcher.ControllersChanged += (_, _) => RenderControllersAndRulesUi();
        _automationWatcher.AutomationNotification += OnAutomationNotification;

        _loc.LanguageChanged += (_, _) =>
        {
            _trayService.UpdateTooltip(_loc["TrayTooltip"]);
            PortStatusSubtitle.Text = _loc["PortDefaultDesc"];
            PortStatusSubtitle.Foreground = TextSecondaryBrush;
            SecondaryPortStatusSubtitle.Text = _loc["SecondaryPortDefaultDesc"];
            SecondaryPortStatusSubtitle.Foreground = TextSecondaryBrush;
            if (_lastStatus is not null)
            {
                UpdateUiFromStatus(_lastStatus);
            }
            RenderControllersAndRulesUi();
        };

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(45)
        };
        _pollTimer.Tick += PollTimer_Tick;

        SourceInitialized += (_, _) =>
        {
            _trayService.Initialize(this, _loc["TrayTooltip"]);
            _trayService.TrayLeftClicked += (_, _) => ToggleWindowVisibility();
            _trayService.TrayRightClicked += (_, _) => ShowTrayContextMenu();
        };

        Loaded += async (_, _) =>
        {
            DsuStatusDto status = await _ipcClient.GetStatusAsync();
            _loc.SetLanguage(status.Language);
            UpdateUiFromStatus(status);
            await RefreshProfilesAsync(status.ActiveProfileName);

            _suppressUiEvents = true;
            AutoControllerSwitchToggle.IsChecked = _automationWatcher.Config.AutoSwitchByController;
            AutoEmulatorSwitchToggle.IsChecked = _automationWatcher.Config.AutoSwitchByEmulator;
            _suppressUiEvents = false;

            _automationWatcher.Start();
            RenderControllersAndRulesUi();

            _pollTimer.Start();
            _gamepadService.Start();
        };

        Closed += (_, _) =>
        {
            _snackbarTimer?.Stop();
            _automationWatcher.Dispose();
            _hudToastService.Dispose();
            _gamepadService.Dispose();
            _pollTimer.Stop();
            _trayService.Dispose();
            _ipcClient.Dispose();
        };
    }

    public IReadOnlyList<Control> GetActiveTabNavigableControls()
    {
        if (ControllersTabButton.IsChecked == true)
        {
            return
            [
                AutoControllerSwitchToggle,
                CalibrateButton
            ];
        }

        if (ProfilesTabButton.IsChecked == true)
        {
            return
            [
                ProfileComboBox,
                DeleteProfileButton,
                NewProfileNameTextBox,
                SaveProfileButton,
                ImportProfileButton,
                ExportProfileButton,
                AutoEmulatorSwitchToggle,
                CustomExeTextBox,
                AddCustomExeRuleButton,
                CalibrateButton
            ];
        }

        if (SettingsTabButton.IsChecked == true)
        {
            if (MultiPortClusterToggle.IsChecked == true)
            {
                return
                [
                    LanguageComboBox,
                    PortTextBox,
                    ApplyPortButton,
                    MultiPortClusterToggle,
                    SecondaryPortTextBox,
                    ApplySecondaryPortButton,
                    StartupToggle,
                    CloseToTrayToggle,
                    HudNotificationsToggle,
                    OpenDataFolderButton,
                    ResetAllSettingsButton,
                    CalibrateButton
                ];
            }

            return
            [
                LanguageComboBox,
                PortTextBox,
                ApplyPortButton,
                MultiPortClusterToggle,
                StartupToggle,
                CloseToTrayToggle,
                HudNotificationsToggle,
                OpenDataFolderButton,
                ResetAllSettingsButton,
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
            SensitivitySlider,
            DeadzoneSlider,
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

    private MenuItem CreateTrayMenuItem(
        Style itemStyle,
        string iconKey,
        string labelText,
        Brush? iconBrush = null,
        Brush? textBrush = null,
        string? rightBadgeText = null,
        Brush? rightBadgeBrush = null,
        bool isStrokeIcon = true)
    {
        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Brush resolvedIconBrush = iconBrush ?? TextSecondaryBrush;
        var iconPath = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource(iconKey),
            Width = 11,
            Height = 11,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (isStrokeIcon)
        {
            iconPath.Stroke = resolvedIconBrush;
            iconPath.StrokeThickness = 1.4;
            iconPath.StrokeStartLineCap = PenLineCap.Round;
            iconPath.StrokeEndLineCap = PenLineCap.Round;
            iconPath.StrokeLineJoin = PenLineJoin.Round;
        }
        else
        {
            iconPath.Fill = resolvedIconBrush;
        }

        Grid.SetColumn(iconPath, 0);
        rowGrid.Children.Add(iconPath);

        var titleBlock = new TextBlock
        {
            Text = labelText,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = textBrush ?? TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(titleBlock, 1);
        rowGrid.Children.Add(titleBlock);

        if (!string.IsNullOrEmpty(rightBadgeText))
        {
            var badgeBorder = new Border
            {
                CornerRadius = new CornerRadius(2.5),
                Padding = new Thickness(4, 1, 4, 1),
                Background = BgSubPanelBrush,
                BorderBrush = BorderInputBrush,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = rightBadgeText,
                    FontFamily = (FontFamily)FindResource("FontMonospace"),
                    FontSize = 8.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = rightBadgeBrush ?? TextSecondaryBrush
                }
            };
            Grid.SetColumn(badgeBorder, 2);
            rowGrid.Children.Add(badgeBorder);
        }

        return new MenuItem
        {
            Style = itemStyle,
            Header = rowGrid
        };
    }

    private void ShowTrayContextMenu()
    {
        if (_trayContextMenu is not null)
        {
            _trayContextMenu.IsOpen = false;
        }

        var itemStyle = (Style)FindResource("AppTrayMenuItemStyle");
        var sepStyle = (Style)FindResource("AppTraySeparatorStyle");

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("AppTrayContextMenuStyle"),
            Placement = PlacementMode.MousePoint
        };

        var showHideItem = CreateTrayMenuItem(
            itemStyle,
            "IconDashboard",
            IsVisible ? _loc["TrayMenuHideWindow"] : _loc["TrayMenuShowWindow"]);
        showHideItem.Click += (_, _) => ToggleWindowVisibility();
        menu.Items.Add(showHideItem);

        bool isStreaming = _lastStatus?.IsStreamingEnabled ?? true;
        var streamItem = CreateTrayMenuItem(
            itemStyle,
            "IconSignalPulse",
            _loc["TrayMenuStream"],
            iconBrush: isStreaming ? StatusOnlineBrush : TextMutedBrush,
            rightBadgeText: isStreaming ? _loc["TrayBadgeOn"] : _loc["TrayBadgeOff"],
            rightBadgeBrush: isStreaming ? StatusOnlineBrush : TextMutedBrush);
        streamItem.Click += async (_, _) =>
        {
            DsuStatusDto status = await _ipcClient.ToggleStreamingAsync(!isStreaming);
            UpdateUiFromStatus(status);
        };
        menu.Items.Add(streamItem);

        var calibrateItem = CreateTrayMenuItem(
            itemStyle,
            "IconCrosshair",
            _loc["TrayMenuCalibrate"]);
        calibrateItem.Click += async (_, _) => await TriggerCalibrationFromControllerAsync();
        menu.Items.Add(calibrateItem);

        var settingsItem = CreateTrayMenuItem(
            itemStyle,
            "IconSettings",
            _loc["TrayMenuSettings"]);
        settingsItem.Click += (_, _) =>
        {
            ShowFromTray();
            SettingsTabButton.IsChecked = true;
        };
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator { Style = sepStyle });

        var exitItem = CreateTrayMenuItem(
            itemStyle,
            "IconClose",
            _loc["TrayMenuExit"],
            iconBrush: StatusOfflineBrush,
            textBrush: StatusOfflineBrush,
            isStrokeIcon: false);
        exitItem.Click += (_, _) => Close();
        menu.Items.Add(exitItem);

        menu.Closed += (_, _) => _trayService.CompleteContextMenu();

        _trayContextMenu = menu;
        _trayService.PrepareForContextMenu();
        menu.IsOpen = true;
    }

    public void CycleTab(int direction)
    {
        RadioButton[] tabs = [DashboardTabButton, ControllersTabButton, ProfilesTabButton, SettingsTabButton];
        int currentIndex = 0;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (tabs[i].IsChecked == true)
            {
                currentIndex = i;
                break;
            }
        }

        int nextIndex = (currentIndex + direction + tabs.Length) % tabs.Length;
        tabs[nextIndex].IsChecked = true;
    }

    private void NavTab_Checked(object sender, RoutedEventArgs e)
    {
        if (DashboardViewPanel is null ||
            ControllersViewPanel is null ||
            ProfilesViewPanel is null ||
            SettingsViewPanel is null)
        {
            return;
        }

        DashboardViewPanel.Visibility = DashboardTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ControllersViewPanel.Visibility = ControllersTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ProfilesViewPanel.Visibility = ProfilesTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsViewPanel.Visibility = SettingsTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (ControllersTabButton.IsChecked == true || ProfilesTabButton.IsChecked == true)
        {
            RenderControllersAndRulesUi();
        }

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

            // Update the port input fields unless the user is actively typing in them
            if (!PortTextBox.IsKeyboardFocusWithin)
            {
                PortTextBox.Text = status.ServerPort.ToString();
            }

            if (!SecondaryPortTextBox.IsKeyboardFocusWithin)
            {
                SecondaryPortTextBox.Text = status.SecondaryPort.ToString();
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

            CloseToTrayToggle.IsChecked = status.CloseToTray;
            HudNotificationsToggle.IsChecked = status.HudNotificationsEnabled;

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
            SensitivityValueText.Text = $"{status.Sensitivity:0.0}x";

            if (!DeadzoneSlider.IsMouseCaptureWithin)
            {
                DeadzoneSlider.Value = Math.Clamp(status.Deadzone, 0.0f, 3.0f);
            }
            DeadzoneValueText.Text = $"{status.Deadzone:0.0}°/s";

            UpdateAxisBadgeVisual(BadgePitchOffset, BadgePitchOffsetText, status.UsePitchOffset);
            UpdateAxisBadgeVisual(BadgeInvertX, BadgeInvertXText, status.InvertX);
            UpdateAxisBadgeVisual(BadgeInvertY, BadgeInvertYText, status.InvertY);
            UpdateAxisBadgeVisual(BadgeInvertZ, BadgeInvertZText, status.InvertZ);
            UpdateAxisBadgeVisual(BadgeSwapAxes, BadgeSwapAxesText, status.SwapAxes);
            UpdateAxisBadgeVisual(BadgeSensitivity, BadgeSensitivityText, Math.Abs(status.Sensitivity - 1.0f) > 0.04f);
            UpdateAxisBadgeVisual(BadgeDeadzone, BadgeDeadzoneText, status.Deadzone > 0.04f);

            UpdateProfileIndicatorUi(ProfileComboBox.SelectedItem as HandheldProfile, status);

            // 4. Live IMU values (deg/s & G) and Live Gyro Crosshair / Horizon Radar Scope
            PitchValueText.Text = $"{status.GyroPitch:+0.00;-0.00;0.00}°/s";
            YawValueText.Text = $"{status.GyroYaw:+0.00;-0.00;0.00}°/s";
            RollValueText.Text = $"{status.GyroRoll:+0.00;-0.00;0.00}°/s";

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

    private async void OnAutomationNotification(object? sender, (string Title, string Subtitle, string? ProfileToApply, int ConnectedCount) args)
    {
        if (args.ConnectedCount > 4)
        {
            _ipcClient.SetSecondaryClusterActive(true);
        }

        if (!string.IsNullOrWhiteSpace(args.ProfileToApply))
        {
            DsuStatusDto status = await _ipcClient.ApplyProfileAsync(args.ProfileToApply);
            UpdateUiFromStatus(status);
        }

        RenderControllersAndRulesUi();
        ShowToastNotification(args.Title, args.Subtitle, isError: false, showHudOverlay: true);
    }

    private void AutoControllerSwitchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        _automationWatcher.Config.AutoSwitchByController = AutoControllerSwitchToggle.IsChecked == true;
        _automationWatcher.SaveConfig();
    }

    private void AutoEmulatorSwitchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        _automationWatcher.Config.AutoSwitchByEmulator = AutoEmulatorSwitchToggle.IsChecked == true;
        _automationWatcher.SaveConfig();
    }

    private void AddCustomExeRuleButton_Click(object sender, RoutedEventArgs e)
    {
        AddCustomExeRule();
    }

    private void CustomExeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddCustomExeRule();
            Keyboard.ClearFocus();
        }
    }

    private void AddCustomExeRule()
    {
        string rawInput = CustomExeTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(rawInput))
            return;

        string cleanProc = rawInput.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        string activeProfile = _lastStatus?.ActiveProfileName ?? "Standard Landscape";
        string friendlyDisplayName = AutomationConfig.ResolveFriendlyProcessDisplayName(cleanProc);

        _automationWatcher.AddOrUpdateEmulatorRule(
            processName: cleanProc,
            displayName: friendlyDisplayName,
            profileName: activeProfile,
            isEnabled: true);

        CustomExeTextBox.Text = string.Empty;
        RenderControllersAndRulesUi();
        ShowToastNotification(_loc["ToastEmulatorRuleAddedTitle"], $"{cleanProc}.exe → {activeProfile}");
    }

    private void RenderControllersAndRulesUi()
    {
        if (ControllersListPanel is null || EmulatorRulesListPanel is null)
            return;

        int basePort = _lastStatus?.ServerPort ?? 26760;
        int secondaryPort = _lastStatus?.SecondaryPort ?? 26762;
        IReadOnlyList<KnownControllerConfig> controllers = _automationWatcher.ScanControllers(basePort, secondaryPort);

        int connectedCount = controllers.Count(c => c.IsConnected);
        if (connectedCount > 4 && _lastStatus?.IsSecondaryClusterActive == false)
        {
            _ipcClient.SetSecondaryClusterActive(true);
        }

        ControllersListPanel.Children.Clear();

        foreach (KnownControllerConfig ctrl in controllers)
        {
            var cardBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(11, 9, 11, 9),
                Margin = new Thickness(0, 0, 0, 7),
                Background = BgSubPanelBrush,
                BorderBrush = ctrl.IsConnected ? StatusOnlineBrush : BorderInputBrush,
                BorderThickness = new Thickness(1)
            };

            var rootStack = new StackPanel();

            // Row 1: Controller Name + Online/Offline Badge + Forget Button (if offline)
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel();
            var nameText = new TextBlock
            {
                Text = ctrl.DisplayName,
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimaryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleStack.Children.Add(nameText);

            var statusBadgeText = new TextBlock
            {
                Text = ctrl.IsConnected
                    ? _loc.Format("ControllerBadgeConnectedFormat", ctrl.AssignedPlayerIndex + 1, ctrl.AssignedUdpPort)
                    : _loc["ControllerBadgeOffline"],
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = ctrl.IsConnected ? StatusOnlineBrush : TextMutedBrush,
                Margin = new Thickness(0, 2, 0, 0)
            };
            titleStack.Children.Add(statusBadgeText);

            var hwIdText = new TextBlock
            {
                Text = ctrl.HardwareId,
                FontSize = 8.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = TextMutedBrush,
                Margin = new Thickness(0, 1, 0, 0)
            };
            titleStack.Children.Add(hwIdText);

            Grid.SetColumn(titleStack, 0);
            topGrid.Children.Add(titleStack);

            if (!ctrl.IsConnected && !string.Equals(ctrl.HardwareId, "BUILTIN_HANDHELD_IMU", StringComparison.OrdinalIgnoreCase))
            {
                var forgetBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("AppGhostButtonStyle"),
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Top,
                    ToolTip = _loc["ControllerForgetTooltip"]
                };
                string hwKey = ctrl.HardwareId;
                forgetBtn.Click += (_, _) => _automationWatcher.ForgetController(hwKey);
                Grid.SetColumn(forgetBtn, 1);
                topGrid.Children.Add(forgetBtn);
            }

            rootStack.Children.Add(topGrid);

            // Row 2: Assigned Profile Selector
            var bottomGrid = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var profileLabel = new TextBlock
            {
                Text = _loc["ControllerAutoProfileLabel"],
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(profileLabel, 0);
            bottomGrid.Children.Add(profileLabel);

            var profileCombo = new ComboBox
            {
                Style = (Style)FindResource("AppComboBoxStyle"),
                ItemsSource = _profiles,
                DisplayMemberPath = "Name",
                SelectedValuePath = "Name",
                SelectedValue = ctrl.AssignedProfileName
            };

            profileCombo.SelectionChanged += async (_, _) =>
            {
                if (profileCombo.SelectedItem is HandheldProfile selectedProf)
                {
                    ctrl.AssignedProfileName = selectedProf.Name;
                    _automationWatcher.SaveConfig();

                    if (ctrl.IsConnected)
                    {
                        DsuStatusDto status = await _ipcClient.ApplyProfileAsync(selectedProf.Name);
                        UpdateUiFromStatus(status);
                        ShowToastNotification(
                            $"🎮 {ctrl.DisplayName}",
                            _loc.Format("ToastControllerProfileAppliedFormat", selectedProf.Name));
                    }
                    else
                    {
                        ShowToastNotification(
                            $"🎮 {ctrl.DisplayName}",
                            _loc.Format("ToastControllerProfileSavedFormat", selectedProf.Name));
                    }
                }
            };

            Grid.SetColumn(profileCombo, 1);
            bottomGrid.Children.Add(profileCombo);

            rootStack.Children.Add(bottomGrid);
            cardBorder.Child = rootStack;
            ControllersListPanel.Children.Add(cardBorder);
        }

        // Populate Emulator (.exe) Rules List
        EmulatorRulesListPanel.Children.Clear();
        if (_automationWatcher.Config.EmulatorRules.Count == 0)
        {
            var emptyBorder = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 6),
                Background = BgSubPanelBrush,
                BorderBrush = BorderInputBrush,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = _loc["EmulatorEmptyStateDesc"],
                    FontSize = 10,
                    Foreground = TextMutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center
                }
            };
            EmulatorRulesListPanel.Children.Add(emptyBorder);
            return;
        }

        foreach (EmulatorProfileRule rule in _automationWatcher.Config.EmulatorRules)
        {
            var ruleBorder = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Background = BgSubPanelBrush,
                BorderBrush = BorderInputBrush,
                BorderThickness = new Thickness(1)
            };

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var ruleToggle = new CheckBox
            {
                Style = (Style)FindResource("AppToggleSwitchStyle"),
                IsChecked = rule.IsEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0)
            };
            ruleToggle.Click += (_, _) =>
            {
                rule.IsEnabled = ruleToggle.IsChecked == true;
                _automationWatcher.SaveConfig();
            };
            Grid.SetColumn(ruleToggle, 0);
            rowGrid.Children.Add(ruleToggle);

            var labelStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            labelStack.Children.Add(new TextBlock
            {
                Text = rule.DisplayName,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimaryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            labelStack.Children.Add(new TextBlock
            {
                Text = $"{rule.ProcessName}.exe",
                FontSize = 8.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = TextMutedBrush
            });
            Grid.SetColumn(labelStack, 1);
            rowGrid.Children.Add(labelStack);

            var ruleProfileCombo = new ComboBox
            {
                Style = (Style)FindResource("AppComboBoxStyle"),
                ItemsSource = _profiles,
                DisplayMemberPath = "Name",
                SelectedValuePath = "Name",
                SelectedValue = rule.ProfileName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            ruleProfileCombo.SelectionChanged += (_, _) =>
            {
                if (ruleProfileCombo.SelectedItem is HandheldProfile selectedProf)
                {
                    rule.ProfileName = selectedProf.Name;
                    _automationWatcher.SaveConfig();
                    ShowToastNotification(
                        $"🚀 {rule.DisplayName}",
                        _loc.Format("ToastEmulatorRuleUpdatedFormat", selectedProf.Name));
                }
            };
            Grid.SetColumn(ruleProfileCombo, 2);
            rowGrid.Children.Add(ruleProfileCombo);

            var removeBtn = new Button
            {
                Content = "✕",
                Style = (Style)FindResource("AppGhostButtonStyle"),
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = _loc["EmulatorRemoveTooltip"]
            };
            string procKey = rule.ProcessName;
            removeBtn.Click += (_, _) => _automationWatcher.RemoveEmulatorRule(procKey);
            Grid.SetColumn(removeBtn, 3);
            rowGrid.Children.Add(removeBtn);

            ruleBorder.Child = rowGrid;
            EmulatorRulesListPanel.Children.Add(ruleBorder);
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

    private async void HudNotificationsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        DsuStatusDto status = await _ipcClient.UpdatePreferencesAsync(hudNotificationsEnabled: HudNotificationsToggle.IsChecked == true);
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
            DsuStatusDto status = await _ipcClient.ResetAllSettingsAsync();
            _automationWatcher.Config.AutoSwitchByController = true;
            _automationWatcher.Config.AutoSwitchByEmulator = true;
            _automationWatcher.SaveConfig();

            _suppressUiEvents = true;
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
        SensitivityValueText.Text = $"{sensitivity:0.0}x";
        DeadzoneValueText.Text = $"{deadzone:0.0}°/s";

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

    private void AppBarProfileButton_Click(object sender, RoutedEventArgs e)
    {
        ProfilesTabButton.IsChecked = true;
        if (ProfileComboBox.SelectedItem is HandheldProfile selected &&
            _lastStatus is not null &&
            !DoesProfileMatchStatus(selected, _lastStatus))
        {
            NewProfileNameTextBox.Focus();
        }
    }

    private static bool DoesProfileMatchStatus(HandheldProfile profile, DsuStatusDto status)
    {
        return profile.InvertX == status.InvertX
            && profile.InvertY == status.InvertY
            && profile.InvertZ == status.InvertZ
            && profile.SwapAxes == status.SwapAxes
            && profile.UsePitchOffset == status.UsePitchOffset
            && Math.Abs(profile.Sensitivity - status.Sensitivity) <= 0.04f
            && Math.Abs(profile.Deadzone - status.Deadzone) <= 0.04f;
    }

    private void UpdateProfileIndicatorUi(HandheldProfile? activeProfile, DsuStatusDto? status)
    {
        activeProfile ??= _profiles.FirstOrDefault(p => string.Equals(p.Name, status?.ActiveProfileName, StringComparison.OrdinalIgnoreCase));
        string baseName = activeProfile?.Name ?? status?.ActiveProfileName ?? "Standard Landscape";
        bool isModified = activeProfile is not null && status is not null && !DoesProfileMatchStatus(activeProfile, status);

        if (isModified)
        {
            AppBarProfileNameText.Text = $"{baseName} *";
            AppBarProfileButton.BorderBrush = AccentPrimaryBrush;
            AppBarProfileButton.Background = AccentDimBrush;
            AppBarProfileNameText.Foreground = TextOnAccentBrush;
            AppBarProfileIcon.Stroke = AccentPrimaryBrush;
            AppBarProfileButton.ToolTip = _loc["AppBarProfileModifiedTooltip"];

            ProfileStatusBadgeText.Text = _loc["ProfileBadgeModified"];
            ProfileStatusBadgeText.Foreground = AccentPrimaryBrush;
        }
        else
        {
            AppBarProfileNameText.Text = baseName;
            AppBarProfileButton.BorderBrush = BorderInputBrush;
            AppBarProfileButton.Background = BgSubPanelBrush;
            AppBarProfileNameText.Foreground = TextSecondaryBrush;
            AppBarProfileIcon.Stroke = StatusOnlineBrush;
            AppBarProfileButton.ToolTip = _loc["AppBarProfileTooltip"];

            bool isCustom = activeProfile is not null && !IsSystemPreset(activeProfile);
            ProfileStatusBadgeText.Text = isCustom ? _loc["ProfileBadgeCustom"] : _loc["ProfileBadgeSystem"];
            ProfileStatusBadgeText.Foreground = isCustom ? StatusOnlineBrush : TextSecondaryBrush;
        }
    }

    private static bool IsSystemPreset(HandheldProfile profile)
    {
        return string.Equals(profile.Author, "System", StringComparison.OrdinalIgnoreCase)
            || string.Equals(profile.Name, "Standard Landscape", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatProfileTagSummary(HandheldProfile profile)
    {
        var tags = new List<string>();
        if (profile.UsePitchOffset) tags.Add("0° PITCH");
        if (profile.InvertX) tags.Add("INV-X");
        if (profile.InvertY) tags.Add("INV-Y");
        if (profile.InvertZ) tags.Add("INV-Z");
        if (profile.SwapAxes) tags.Add("SWAP X⇄Y");
        if (Math.Abs(profile.Sensitivity - 1.0f) > 0.04f) tags.Add($"{profile.Sensitivity:0.0}x");
        if (profile.Deadzone > 0.04f) tags.Add($"DZ {profile.Deadzone:0.0}°/s");

        return tags.Count > 0
            ? string.Join("  •  ", tags)
            : "RAW (1.0x / 0.0° DZ)";
    }

    private void UpdateSelectedProfileDetails(HandheldProfile profile)
    {
        ProfileDescriptionText.Text = FormatProfileTagSummary(profile);
        DeleteProfileButton.IsEnabled = !IsSystemPreset(profile);
        UpdateProfileIndicatorUi(profile, _lastStatus);
    }

    public void ShowToastNotification(string title, string subtitle, bool isError = false, bool showHudOverlay = false)
    {
        Brush accent = isError ? StatusOfflineBrush : StatusOnlineBrush;
        AppSnackbarAccentBar.Background = accent;
        AppSnackbarBorder.BorderBrush = accent;
        AppSnackbarTitleText.Text = title;
        AppSnackbarMessageText.Text = subtitle;
        AppSnackbarBorder.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slideUp = new DoubleAnimation(14.0, 0.0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        AppSnackbarBorder.BeginAnimation(OpacityProperty, fadeIn);
        AppSnackbarTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);

        _snackbarTimer?.Stop();
        _snackbarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
        _snackbarTimer.Tick += (_, _) =>
        {
            _snackbarTimer?.Stop();
            var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) =>
            {
                AppSnackbarBorder.Visibility = Visibility.Collapsed;
            };
            AppSnackbarBorder.BeginAnimation(OpacityProperty, fadeOut);
        };
        _snackbarTimer.Start();

        if ((_lastStatus?.HudNotificationsEnabled ?? true) &&
            (!IsVisible || WindowState == WindowState.Minimized || showHudOverlay))
        {
            _hudToastService.ShowHudToast(title, subtitle, isError);
        }
    }

    private void ShowProfileStatus(string message, bool isError = false, string? subtitle = null)
    {
        string detail = subtitle ?? (ProfileComboBox.SelectedItem is HandheldProfile p
            ? FormatProfileTagSummary(p)
            : $"UDP 0.0.0.0:{_lastStatus?.ServerPort ?? 26760}");

        ShowToastNotification(message, detail, isError);
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

            RenderControllersAndRulesUi();
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
            UsePitchOffset = PitchOffsetToggle.IsChecked == true,
            Sensitivity = (float)Math.Round(SensitivitySlider.Value, 1),
            Deadzone = (float)Math.Round(DeadzoneSlider.Value, 1)
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
                UsePitchOffset = PitchOffsetToggle.IsChecked == true,
                Sensitivity = (float)Math.Round(SensitivitySlider.Value, 1),
                Deadzone = (float)Math.Round(DeadzoneSlider.Value, 1)
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
        if (CloseToTrayToggle.IsChecked == true && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            HideToTray();
            return;
        }

        Close();
    }
}
