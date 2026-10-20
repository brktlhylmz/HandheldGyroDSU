using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandheldDsuWidget.Services;

namespace HandheldDsuWidget;

public partial class MainWindow : Window
{
    private readonly DsuIpcClient _ipcClient = new();
    private readonly GameBarWidgetAdapter _gameBarAdapter = new();
    private readonly SystemTrayService _trayService = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly bool _startedMinimized;

    private bool _suppressUiEvents;
    private bool _isPollBusy;

    private static readonly SolidColorBrush GreenLedBrush = new(Color.FromRgb(0x00, 0xE6, 0x76));
    private static readonly SolidColorBrush RedLedBrush = new(Color.FromRgb(0xFF, 0x4D, 0x4F));
    private static readonly SolidColorBrush MutedBrush = new(Color.FromRgb(0x9A, 0xA4, 0xB2));

    public MainWindow() : this(startMinimized: false)
    {
    }

    public MainWindow(bool startMinimized)
    {
        _startedMinimized = startMinimized;
        InitializeComponent();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _pollTimer.Tick += PollTimer_Tick;

        SourceInitialized += (_, _) =>
        {
            _trayService.Initialize(this, "Handheld Gyro DSU (Click to Show/Hide)");
            _trayService.TrayLeftClicked += (_, _) => ToggleWindowVisibility();
            _trayService.TrayRightClicked += (_, _) => ShowFromTray();
        };

        Loaded += async (_, _) =>
        {
            DsuStatusDto status = await _ipcClient.GetStatusAsync();
            UpdateUiFromStatus(status);
            _pollTimer.Start();
        };

        Closed += (_, _) =>
        {
            _pollTimer.Stop();
            _trayService.Dispose();
            _ipcClient.Dispose();
        };
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
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
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

    private void UpdateUiFromStatus(DsuStatusDto status)
    {
        _suppressUiEvents = true;
        try
        {
            EngineSubtitleText.Text = $"UDP 0.0.0.0:{status.ServerPort} • IPC 127.0.0.1:{status.IpcPort}";

            // Update the port input field unless the user is actively typing in it
            if (!PortTextBox.IsKeyboardFocusWithin)
            {
                PortTextBox.Text = status.ServerPort.ToString();
            }

            // 1. Emulator connection status indicator
            if (status.IsEmulatorConnected)
            {
                StatusLedGlow.Fill = GreenLedBrush;
                StatusLedDot.Fill = GreenLedBrush;
                ConnectionStatusTitle.Text = "Emülatör Bağlı (Aktif Yayın)";
                ConnectionStatusDetail.Text = string.Join(", ", status.ConnectedClients);
                PacketCounterText.Text = $"{status.TotalPacketsSent:N0} pkt";
            }
            else
            {
                StatusLedGlow.Fill = RedLedBrush;
                StatusLedDot.Fill = RedLedBrush;
                ConnectionStatusTitle.Text = "DSU Motoru Aktif • Emülatör Bekleniyor";
                ConnectionStatusDetail.Text = $"Eden / Yuzu / Cemu -> 127.0.0.1:{status.ServerPort}";
                PacketCounterText.Text = status.TotalPacketsSent > 0
                    ? $"{status.TotalPacketsSent:N0} pkt"
                    : "HAZIR";
            }

            // 2. DSU stream and Start with Windows switches
            GyroStreamToggle.IsChecked = status.IsStreamingEnabled;
            GyroToggleSubtitle.Text = status.IsStreamingEnabled
                ? "Aktif • Emülatöre hareket verisi gönderiliyor"
                : "Duraklatıldı • DSU veri akışı kapalı";

            StartupToggle.IsChecked = status.StartWithWindows;
            StartupToggleSubtitle.Text = status.StartWithWindows
                ? "Açık • Windows başlangıcında tepside (gizli) başlar"
                : "Kapalı • Yalnızca manuel çalıştırıldığında açılır";

            // 3. Active device profile selection
            int targetIndex = 0;
            if (status.ActiveProfile.Contains("Legion", StringComparison.OrdinalIgnoreCase))
                targetIndex = 1;
            else if (status.ActiveProfile.Contains("Claw", StringComparison.OrdinalIgnoreCase))
                targetIndex = 2;

            if (DeviceProfileComboBox.SelectedIndex != targetIndex && !DeviceProfileComboBox.IsDropDownOpen)
            {
                DeviceProfileComboBox.SelectedIndex = targetIndex;
            }

            InvertXToggle.IsChecked = status.InvertX;
            InvertYToggle.IsChecked = status.InvertY;
            InvertZToggle.IsChecked = status.InvertZ;
            SwapAxesToggle.IsChecked = status.SwapAxes;
            PitchOffsetToggle.IsChecked = status.UsePitchOffset;

            // 4. Live IMU values (deg/s & G)
            PitchValueText.Text = $"{status.GyroPitch:+0.00;-0.00;0.00}°/s";
            YawValueText.Text = $"{status.GyroYaw:+0.00;-0.00;0.00}°/s";
            RollValueText.Text = $"{status.GyroRoll:+0.00;-0.00;0.00}°/s";

            BiasStatusText.Text =
                $"Acc (X/Y/Z): {status.AccX:F2}, {status.AccY:F2}, {status.AccZ:F2}G | Bias: {status.BiasX:+0.00;-0.00;0.00}, {status.BiasY:+0.00;-0.00;0.00}, {status.BiasZ:+0.00;-0.00;0.00}";

            // 5. Calibration button state
            if (status.IsCalibrating)
            {
                CalibrateButton.IsEnabled = false;
                CalibrateButton.Content = $"⏳ %{status.CalibrationProgress}";
            }
            else
            {
                CalibrateButton.IsEnabled = true;
                CalibrateButton.Content = "🎯 KALİBRE ET";
            }
        }
        finally
        {
            _suppressUiEvents = false;
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
            PortStatusSubtitle.Text = "Hata: Port 1024 - 65535 arasında olmalı!";
            PortStatusSubtitle.Foreground = RedLedBrush;
            return;
        }

        ApplyPortButton.IsEnabled = false;
        try
        {
            var (success, status) = await _ipcClient.ChangePortAsync(newPort);
            if (success)
            {
                PortStatusSubtitle.Text = $"Aktif Port: {newPort} (settings.json kaydedildi)";
                PortStatusSubtitle.Foreground = GreenLedBrush;
            }
            else
            {
                PortStatusSubtitle.Text = status.Message;
                PortStatusSubtitle.Foreground = RedLedBrush;
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

    private async void CalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        CalibrateButton.IsEnabled = false;
        CalibrateButton.Content = "⏳ %0";

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
                CalibrateButton.Content = "🎯 KALİBRE ET";
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

    private async void DeviceProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || !IsLoaded)
            return;

        if (DeviceProfileComboBox.SelectedItem is ComboBoxItem item && item.Tag is string profileKey)
        {
            DsuStatusDto status = await _ipcClient.SetProfileAsync(profileKey);
            UpdateUiFromStatus(status);
        }
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

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinButton.Foreground = Topmost ? GreenLedBrush : MutedBrush;
    }

    private void MinimizeToTrayButton_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
