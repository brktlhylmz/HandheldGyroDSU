using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HandheldDsuWidget.Services;

/// <summary>
/// Ekstra bağımlılık gerektirmeden doğrudan Win32 Shell_NotifyIconW API'si ile
/// Windows Sistem Tepsisi (System Tray) ikonu oluşturur ve tıklama olaylarını yönetir.
/// </summary>
public sealed class SystemTrayService : IDisposable
{
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 260;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private static readonly IntPtr IDI_APPLICATION = new(32512);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    private HwndSource? _hwndSource;
    private NOTIFYICONDATA _notifyData;
    private bool _isAdded;

    public event EventHandler? TrayLeftClicked;
    public event EventHandler? TrayRightClicked;

    public void Initialize(Window window, string tooltip = "Handheld Gyro DSU (Sol Tık: Göster/Gizle)")
    {
        if (_isAdded)
            return;

        var helper = new WindowInteropHelper(window);
        IntPtr hwnd = helper.EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        IntPtr hIcon = LoadIcon(IntPtr.Zero, IDI_APPLICATION);

        _notifyData = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = tooltip
        };

        _isAdded = Shell_NotifyIconW(NIM_ADD, ref _notifyData);
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_isAdded)
            return;

        _notifyData.szTip = tooltip.Length > 120 ? tooltip[..120] : tooltip;
        Shell_NotifyIconW(NIM_MODIFY, ref _notifyData);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                TrayLeftClicked?.Invoke(this, EventArgs.Empty);
                handled = true;
            }
            else if (mouseMsg == WM_RBUTTONUP)
            {
                TrayRightClicked?.Invoke(this, EventArgs.Empty);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_isAdded)
        {
            Shell_NotifyIconW(NIM_DELETE, ref _notifyData);
            _isAdded = false;
        }

        _hwndSource?.RemoveHook(WndProc);
    }
}
