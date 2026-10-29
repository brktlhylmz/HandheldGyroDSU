using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly UpdateService _updateService = new();
    private GitHubReleaseInfo? _pendingUpdateRelease;
    private bool _isUpdateCheckInProgress;
    private string? _lastAutoAttemptedVersion;
    private DateTime _lastUpdateCheckTime = DateTime.MinValue;
    private readonly GamepadNavigationService _gamepadService;
    private readonly GyroMouseService _gyroMouseService;
    private IntPtr _windowHwnd;
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _autoUpdateTimer;
    private readonly bool _startedMinimized;

    private List<HandheldProfile> _profiles = [];
    private DispatcherTimer? _snackbarTimer;
    private string? _currentSnackbarActionUrl;
    private ContextMenu? _trayContextMenu;
    private bool _suppressUiEvents;
    private bool _isPollBusy;
    private bool _isPinnedByUser;
    private int _backgroundIdleTickCounter;
    private DsuStatusDto? _lastStatus;
    private int _lastSyncedServerPort = -1;
    private int _lastSyncedSecondaryPort = -1;
    private string? _lastTrayTip;
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
        UpdateService.CleanupOldUpdateFiles();
        InitializeComponent();
        CurrentVersionBadgeText.Text = $"v{UpdateService.GetCurrentVersion()}";

        _gamepadService = new GamepadNavigationService(this);
        _gyroMouseService = new GyroMouseService(_ipcClient, () => _windowHwnd != IntPtr.Zero && GetForegroundWindow() == _windowHwnd);
        _gyroMouseService.TriggerButtonBound += OnGyroMouseTriggerButtonBound;

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
            RefreshRpcs3PatchStatusUi();
            RefreshDolphinConfigStatusUi();
        };

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(45)
        };
        _pollTimer.Tick += PollTimer_Tick;

        _autoUpdateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(1)
        };
        _autoUpdateTimer.Tick += async (_, _) =>
        {
            if (_lastStatus?.AutoUpdateEnabled == true)
            {
                await CheckForUpdatesInternalAsync(silentIfUpToDate: true);
            }
        };

        SourceInitialized += (_, _) =>
        {
            _windowHwnd = new WindowInteropHelper(this).Handle;
            _trayService.Initialize(this, _loc["TrayTooltip"]);
            _trayService.TrayLeftClicked += (_, _) => ToggleWindowVisibility();
            _trayService.TrayRightClicked += (_, _) => ShowTrayContextMenu();
            _trayService.PowerResumed += async (_, _) =>
            {
                DsuStatusDto resumedStatus = await _ipcClient.UpdatePreferencesAsync(rebindSensors: true);
                UpdateUiFromStatus(resumedStatus);
                _automationWatcher.ScanControllers(resumedStatus.ServerPort, resumedStatus.SecondaryPort);
                RenderControllersAndRulesUi();
                RefreshRpcs3PatchStatusUi();
                RefreshDolphinConfigStatusUi();
            };
        };

        Loaded += async (_, _) =>
        {
            DsuStatusDto status = await _ipcClient.GetStatusAsync();
            _loc.SetLanguage(status.Language);
            UpdateUiFromStatus(status);
            await RefreshProfilesAsync(status.ActiveProfileName);
            _automationWatcher.SetBaseProfileName(status.ActiveProfileName);

            _suppressUiEvents = true;
            AutoControllerSwitchToggle.IsChecked = _automationWatcher.Config.AutoSwitchByController;
            AutoEmulatorSwitchToggle.IsChecked = _automationWatcher.Config.AutoSwitchByEmulator;
            _suppressUiEvents = false;

            if (GameBarOverlayToggle.Parent is FrameworkElement gbGrid && gbGrid.Parent is Panel sStack) { int idx = sStack.Children.IndexOf(gbGrid); gbGrid.Visibility = Visibility.Collapsed; if (idx > 0 && sStack.Children[idx - 1] is FrameworkElement div) div.Visibility = Visibility.Collapsed; }
            _automationWatcher.Start();
            RenderControllersAndRulesUi();
            RefreshRpcs3PatchStatusUi();
            RefreshDolphinConfigStatusUi();

            _pollTimer.Start();
            _autoUpdateTimer.Start();
            _gamepadService.Start();

            // Check if application was just relaunched after an update
            string[] launchArgs = Environment.GetCommandLineArgs();
            for (int i = 0; i < launchArgs.Length; i++)
            {
                if (string.Equals(launchArgs[i], "--just-updated", StringComparison.OrdinalIgnoreCase))
                {
                    string updatedVersion = (i + 1 < launchArgs.Length) ? launchArgs[i + 1] : "latest";
                    string? releaseUrl = (i + 2 < launchArgs.Length) ? launchArgs[i + 2] : null;
                    if (string.IsNullOrWhiteSpace(releaseUrl))
                    {
                        releaseUrl = "https://github.com/brktlhylmz/HandheldGyroDSU/releases";
                    }

                    ShowToastNotification(
                        _loc.Format("ToastUpdateInstalledTitleFormat", updatedVersion.TrimStart('v', 'V')),
                        _loc["ToastUpdateInstalledDesc"],
                        isError: false,
                        showHudOverlay: true,
                        actionUrl: releaseUrl);
                    break;
                }
            }

            // Prompt user about automatic updates on first launch
            await CheckAndPromptAutoUpdateAsync(status);

#if !DEBUG
            _ = CheckForUpdatesInternalAsync(silentIfUpToDate: true);
#endif

            _gyroMouseService.Start();
        };

        Closed += (_, _) =>
        {
            _snackbarTimer?.Stop();
            _autoUpdateTimer.Stop();
            _gameBarAdapter.Dispose(); _loopbackTester.Dispose(); _updateService.Dispose();
            _automationWatcher.Dispose();
            _hudToastService.Dispose();
            _gamepadService.Dispose();
            _gyroMouseService.Dispose();
            _pollTimer.Stop();
            _trayService.Dispose();
            _ipcClient.Dispose();
        };
    }

    public bool IsCapturingControllerButton =>
        !string.IsNullOrWhiteSpace(_automationWatcher.RecordingHardwareId) ||
        _gyroMouseService.IsListeningForTrigger;

    public IReadOnlyList<Control> GetActiveTabNavigableControls()
    {
        if (ControllersTabButton.IsChecked == true)
        {
            return
            [
                AutoControllerSwitchToggle,
                AutoEmulatorSwitchToggle,
                RunningAppsComboBox,
                RefreshRunningAppsButton,
                AddRunningAppRuleButton,
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
                DevGyroMouseToggle,
                DevGyroMouseModeComboBox,
                DevGyroMouseBindButton,
                DevGyroMouseSpeedSlider,
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
                    UpdateChannelComboBox,
                    AutoUpdateToggle,
                    CheckUpdateButton,
                    InstallUpdateButton,
                    OpenDataFolderButton,
                    OpenLogsButton,
                    ResetAllSettingsButton,
                    DevSimControllerComboBox,
                    DevSimInjectButton,
                    DevSimClearButton,
                    DevSyntheticWaveToggle,
                    DevLoopbackClientToggle,
                    DevTestHudButton,
                DevRpcs3ApplyPatchButton,
                DevRpcs3RestoreButton,
                DevRpcs3BrowseButton,
                DevDolphinApplyConfigButton,
                DevDolphinBrowseButton,
                DevGyroMouseToggle,
                DevGyroMouseModeComboBox,
                DevGyroMouseBindButton,
                DevGyroMouseSpeedSlider,
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
                TabletopCalibrationGuardToggle,
                UpdateChannelComboBox,
                AutoUpdateToggle,
                CheckUpdateButton,
                InstallUpdateButton,
                OpenDataFolderButton,
                OpenLogsButton,
                ResetAllSettingsButton,
                DevSimControllerComboBox,
                DevSimInjectButton,
                DevSimClearButton,
                DevSyntheticWaveToggle,
                DevLoopbackClientToggle,
                DevTestHudButton,
                DevRpcs3ApplyPatchButton,
                DevRpcs3RestoreButton,
                DevRpcs3BrowseButton,
                DevDolphinApplyConfigButton,
                DevDolphinBrowseButton,
                DevGyroMouseToggle,
                DevGyroMouseModeComboBox,
                DevGyroMouseBindButton,
                DevGyroMouseSpeedSlider,
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
            RefreshRpcs3PatchStatusUi();
        }

        if (SettingsTabButton.IsChecked == true)
        {
            RefreshRpcs3PatchStatusUi();
            RefreshDolphinConfigStatusUi();
        }

        MainScrollViewer?.ScrollToTop();
    }

    public void ShowToastNotification(
        string title,
        string subtitle,
        bool isError = false,
        bool showHudOverlay = false,
        string? actionUrl = null)
    {
        Brush accent = isError ? StatusOfflineBrush : StatusOnlineBrush;
        AppSnackbarAccentBar.Background = accent;
        AppSnackbarBorder.BorderBrush = accent;
        AppSnackbarTitleText.Text = title;
        AppSnackbarMessageText.Text = subtitle;

        _currentSnackbarActionUrl = actionUrl;
        if (!string.IsNullOrWhiteSpace(actionUrl))
        {
            AppSnackbarActionBtn.Visibility = Visibility.Visible;
            AppSnackbarBorder.IsHitTestVisible = true;
        }
        else
        {
            AppSnackbarActionBtn.Visibility = Visibility.Collapsed;
            AppSnackbarBorder.IsHitTestVisible = false;
        }

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
        double displaySeconds = !string.IsNullOrWhiteSpace(actionUrl) ? 6.5 : 2.8;
        _snackbarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(displaySeconds) };
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
                AppSnackbarActionBtn.Visibility = Visibility.Collapsed;
                AppSnackbarBorder.IsHitTestVisible = false;
                _currentSnackbarActionUrl = null;
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

    private void AppSnackbarActionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentSnackbarActionUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_currentSnackbarActionUrl) { UseShellExecute = true });
            }
            catch
            {
                // Ignore browser launch errors
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

    public async System.Threading.Tasks.Task CaptureDocumentationScreenshotsAsync(string outputDir)
    {
        try
        {
            System.IO.Directory.CreateDirectory(outputDir);
            _loc.SetLanguage("en");
            WindowState = WindowState.Normal;
            Width = 520;
            Height = 680;
            DsuStatusDto status = await _ipcClient.UpdatePreferencesAsync(forwardGamepadInputs: true);
            UpdateUiFromStatus(status);
            await System.Threading.Tasks.Task.Delay(450);

            _snackbarTimer?.Stop();
            AppSnackbarBorder.Visibility = Visibility.Collapsed;
            SaveElementPng(this, System.IO.Path.Combine(outputDir, "dashboard.png"));

            ControllersTabButton.IsChecked = true;
            await System.Threading.Tasks.Task.Delay(300);
            _snackbarTimer?.Stop();
            AppSnackbarBorder.Visibility = Visibility.Collapsed;
            SaveElementPng(this, System.IO.Path.Combine(outputDir, "controllers.png"));

            ProfilesTabButton.IsChecked = true;
            await System.Threading.Tasks.Task.Delay(300);
            _snackbarTimer?.Stop();
            AppSnackbarBorder.Visibility = Visibility.Collapsed;
            SaveElementPng(this, System.IO.Path.Combine(outputDir, "profiles.png"));

            SettingsTabButton.IsChecked = true;
            MainScrollViewer?.ScrollToTop();
            await System.Threading.Tasks.Task.Delay(300);
            _snackbarTimer?.Stop();
            AppSnackbarBorder.Visibility = Visibility.Collapsed;
            SaveElementPng(this, System.IO.Path.Combine(outputDir, "settings.png"));

            MainScrollViewer?.ScrollToBottom();
            await System.Threading.Tasks.Task.Delay(350);
            _snackbarTimer?.Stop();
            AppSnackbarBorder.Visibility = Visibility.Collapsed;
            SaveElementPng(this, System.IO.Path.Combine(outputDir, "emulator-tools.png"));
        }
        catch
        {
            // Ignore screenshot capture errors
        }
    }

    private static void SaveElementPng(FrameworkElement element, string filePath)
    {
        element.UpdateLayout();
        int width = Math.Max(1, (int)Math.Round(element.ActualWidth));
        int height = Math.Max(1, (int)Math.Round(element.ActualHeight));
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        rtb.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        encoder.Save(fs);
    }
}
