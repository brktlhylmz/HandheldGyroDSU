using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HandheldDsuServer.Models;
using HandheldDsuWidget.Services;

namespace HandheldDsuWidget;

public partial class MainWindow : Window
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);
    private readonly DsuIpcClient _ipcClient = new();
    private readonly GameBarWidgetAdapter _gameBarAdapter = new();
    private readonly SystemTrayService _trayService = new();
    private readonly HudToastService _hudToastService = new();
    private readonly ControllerAndAppWatcherService _automationWatcher = new();
    private readonly DsuLoopbackTesterService _loopbackTester = new();
    private readonly GamepadNavigationService _gamepadService;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly DispatcherTimer _pollTimer;
    private readonly bool _startedMinimized;

    private List<HandheldProfile> _profiles = [];
    private DispatcherTimer? _snackbarTimer;
    private ContextMenu? _trayContextMenu;
    private bool _suppressUiEvents;
    private bool _isPollBusy;
    private bool _isPinnedByUser;
    private int _backgroundIdleTickCounter;
    private DsuStatusDto? _lastStatus;
    private int _lastSyncedServerPort = -1;
    private int _lastSyncedSecondaryPort = -1;
    private double _radarX;
    private double _radarY;
    private double _radarRoll;

    private Brush? _statusOnlineBrush;
    private Brush? _statusOnlineDimBrush;
    private Brush? _statusOfflineBrush;
    private Brush? _statusOfflineDimBrush;
    private Brush? _textPrimaryBrush;
    private Brush? _textSecondaryBrush;
    private Brush? _textMutedBrush;
    private Brush? _textOnAccentBrush;
    private Brush? _accentPrimaryBrush;
    private Brush? _accentDimBrush;
    private Brush? _bgSubPanelBrush;
    private Brush? _borderInputBrush;

    private Brush StatusOnlineBrush => _statusOnlineBrush ??= (Brush)FindResource("BrushStatusOnline");
    private Brush StatusOnlineDimBrush => _statusOnlineDimBrush ??= (Brush)FindResource("BrushStatusOnlineDim");
    private Brush StatusOfflineBrush => _statusOfflineBrush ??= (Brush)FindResource("BrushStatusOffline");
    private Brush StatusOfflineDimBrush => _statusOfflineDimBrush ??= (Brush)FindResource("BrushStatusOfflineDim");
    private Brush TextPrimaryBrush => _textPrimaryBrush ??= (Brush)FindResource("BrushTextPrimary");
    private Brush TextSecondaryBrush => _textSecondaryBrush ??= (Brush)FindResource("BrushTextSecondary");
    private Brush TextMutedBrush => _textMutedBrush ??= (Brush)FindResource("BrushTextMuted");
    private Brush TextOnAccentBrush => _textOnAccentBrush ??= (Brush)FindResource("BrushTextOnAccent");
    private Brush AccentPrimaryBrush => _accentPrimaryBrush ??= (Brush)FindResource("BrushAccentPrimary");
    private Brush AccentDimBrush => _accentDimBrush ??= (Brush)FindResource("BrushAccentDim");
    private Brush BgSubPanelBrush => _bgSubPanelBrush ??= (Brush)FindResource("BrushBgSubPanel");
    private Brush BorderInputBrush => _borderInputBrush ??= (Brush)FindResource("BrushBorderInput");

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
        _automationWatcher.RecenterHotkeyTriggered += OnRecenterHotkeyTriggered;

        _gameBarAdapter.OverlayShowRequested += (_, _) => ShowForGameBarOverlay();
        _gameBarAdapter.OverlayHideRequested += (_, _) => HideFromGameBarOverlay();

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
            _trayService.PowerResumed += async (_, _) =>
            {
                DsuStatusDto resumedStatus = await _ipcClient.UpdatePreferencesAsync(rebindSensors: true);
                UpdateUiFromStatus(resumedStatus);
                _automationWatcher.ScanControllers(resumedStatus.ServerPort, resumedStatus.SecondaryPort);
                RenderControllersAndRulesUi();
            };
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
            _gameBarAdapter.Dispose(); _loopbackTester.Dispose();
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
                AutoEmulatorSwitchToggle,
                AddCustomExeRuleButton,
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
                    GameBarOverlayToggle,
                    HudNotificationsToggle,
                    TabletopCalibrationGuardToggle,
                    OpenDataFolderButton,
                    ResetAllSettingsButton,
                    DevSimControllerComboBox,
                    DevSimInjectButton,
                    DevSimClearButton,
                    DevSyntheticWaveToggle,
                    DevLoopbackClientToggle,
                    DevTestHudButton,
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
                GameBarOverlayToggle,
                HudNotificationsToggle,
                OpenDataFolderButton,
                ResetAllSettingsButton,
                DevSimControllerComboBox,
                DevSimInjectButton,
                DevSimClearButton,
                DevSyntheticWaveToggle,
                DevLoopbackClientToggle,
                DevTestHudButton,
                CalibrateButton
            ];
        }

        return
        [
            GyroStreamToggle,
            ForwardGamepadToggle,
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

    public void ApplyWindowTopmost(bool isTopmost)
    {
        Topmost = isTopmost;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(
                hwnd,
                isTopmost ? HwndTopmost : HwndNoTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _isPinnedByUser = !_isPinnedByUser;
        ApplyWindowTopmost(_isPinnedByUser);
        PinButton.Background = _isPinnedByUser ? StatusOnlineDimBrush : Brushes.Transparent;
        PinButtonIcon.Fill = _isPinnedByUser ? StatusOnlineBrush : TextSecondaryBrush;
        ShowToastNotification(
            _isPinnedByUser ? _loc["ToastPinEnabledTitle"] : _loc["ToastPinDisabledTitle"],
            _isPinnedByUser ? _loc["ToastPinEnabledDesc"] : _loc["ToastPinDisabledDesc"]);
    }

    private void MinimizeToTrayButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
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
