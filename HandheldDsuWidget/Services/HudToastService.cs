using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HandheldDsuWidget.Services;

/// <summary>
/// Displays a non-activating, click-through (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE) HUD toast
/// in the top-right corner of the primary display without stealing focus from fullscreen games/emulators.
/// </summary>
public sealed class HudToastService : IDisposable
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private Window? _toastWindow;
    private Border? _rootBorder;
    private Border? _accentStrip;
    private TextBlock? _titleText;
    private TextBlock? _messageText;
    private TranslateTransform? _slideTransform;
    private DispatcherTimer? _hideTimer;

    public void ShowHudToast(string title, string message, bool isError = false)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            EnsureToastWindowCreated();
            if (_toastWindow is null || _titleText is null || _messageText is null || _accentStrip is null || _slideTransform is null)
                return;

            Brush accentBrush = (Brush)Application.Current.FindResource(isError ? "BrushStatusOffline" : "BrushStatusOnline");
            _accentStrip.Background = accentBrush;
            _titleText.Text = title;
            _messageText.Text = message;

            Rect workArea = SystemParameters.WorkArea;
            _toastWindow.Left = workArea.Right - _toastWindow.Width - 20;
            _toastWindow.Top = workArea.Top + 20;

            _hideTimer?.Stop();

            if (!_toastWindow.IsVisible)
            {
                _toastWindow.Show();
            }

            // Animate slide-in and fade-in
            var fadeAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var slideAnim = new DoubleAnimation(18.0, 0.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            _toastWindow.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            _slideTransform.BeginAnimation(TranslateTransform.XProperty, slideAnim);

            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
            _hideTimer.Tick += (_, _) =>
            {
                _hideTimer?.Stop();
                HideToastAnimated();
            };
            _hideTimer.Start();
        });
    }

    private void HideToastAnimated()
    {
        if (_toastWindow is null || !_toastWindow.IsVisible)
            return;

        var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            _toastWindow?.Hide();
        };
        _toastWindow.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void EnsureToastWindowCreated()
    {
        if (_toastWindow is not null)
            return;

        _slideTransform = new TranslateTransform(0, 0);

        _accentStrip = new Border
        {
            Width = 3.5,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 10, 0),
            Background = (Brush)Application.Current.FindResource("BrushStatusOnline")
        };

        _titleText = new TextBlock
        {
            FontFamily = (FontFamily)Application.Current.FindResource("FontPrimary"),
            Foreground = (Brush)Application.Current.FindResource("BrushTextPrimary"),
            FontSize = 11.5,
            FontWeight = FontWeights.Bold
        };

        _messageText = new TextBlock
        {
            FontFamily = (FontFamily)Application.Current.FindResource("FontMonospace"),
            Foreground = (Brush)Application.Current.FindResource("BrushTextSecondary"),
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        textStack.Children.Add(_titleText);
        textStack.Children.Add(_messageText);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(_accentStrip, 0);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(_accentStrip);
        grid.Children.Add(textStack);

        _rootBorder = new Border
        {
            Background = (Brush)Application.Current.FindResource("BrushBgHeader"),
            BorderBrush = (Brush)Application.Current.FindResource("BrushBorderWindow"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9, 14, 9),
            RenderTransform = _slideTransform,
            Child = grid
        };

        _toastWindow = new Window
        {
            Width = 290,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            IsHitTestVisible = false,
            Content = _rootBorder
        };

        _toastWindow.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(_toastWindow).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
    }

    public void Dispose()
    {
        _hideTimer?.Stop();
        _toastWindow?.Close();
        _toastWindow = null;
    }
}
