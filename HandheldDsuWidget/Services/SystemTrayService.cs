using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HandheldDsuWidget.Services;

/// <summary>
/// Manages the Windows System Tray icon and click events directly via Win32 Shell_NotifyIconW without external dependencies.
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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, IntPtr lpvBits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    private HwndSource? _hwndSource;
    private NOTIFYICONDATA _notifyData;
    private IntPtr _customIconHandle;
    private bool _isAdded;

    public event EventHandler? TrayLeftClicked;
    public event EventHandler? TrayRightClicked;

    public void Initialize(Window window, string tooltip = "Handheld Gyro DSU (Click: Show/Hide)")
    {
        if (_isAdded)
            return;

        var helper = new WindowInteropHelper(window);
        IntPtr hwnd = helper.EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        _customIconHandle = CreateCustomGyroTrayIcon();
        IntPtr hIcon = _customIconHandle != IntPtr.Zero
            ? _customIconHandle
            : LoadIcon(IntPtr.Zero, IDI_APPLICATION);

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

    public void PrepareForContextMenu()
    {
        if (_notifyData.hWnd != IntPtr.Zero)
        {
            SetForegroundWindow(_notifyData.hWnd);
        }
    }

    public void CompleteContextMenu()
    {
        if (_notifyData.hWnd != IntPtr.Zero)
        {
            PostMessageW(_notifyData.hWnd, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static IntPtr CreateCustomGyroTrayIcon()
    {
        try
        {
            const int size = 32;
            uint[] pixels = new uint[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - 15.5f;
                    float dy = y - 15.5f;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);

                    // Rounded dark badge background (#141821) with crimson ring (#FF0032) and cyan center (#00F0FF)
                    if (dist <= 14.5f)
                    {
                        if (dist is >= 11.2f and <= 13.6f)
                        {
                            pixels[y * size + x] = 0xFFFF0032; // Crimson ring
                        }
                        else if (dist <= 3.2f)
                        {
                            pixels[y * size + x] = 0xFF00F0FF; // Cyan center core
                        }
                        else if ((MathF.Abs(dx) <= 1.1f && dist is >= 5.0f and <= 9.8f) ||
                                 (MathF.Abs(dy) <= 1.1f && dist is >= 5.0f and <= 9.8f))
                        {
                            pixels[y * size + x] = 0xFFF2F5FA; // Crosshair ticks
                        }
                        else
                        {
                            pixels[y * size + x] = 0xFF141821; // Dark slate interior
                        }
                    }
                }
            }

            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                IntPtr hbmColor = CreateBitmap(size, size, 1, 32, handle.AddrOfPinnedObject());
                IntPtr hbmMask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
                try
                {
                    var iconInfo = new ICONINFO
                    {
                        fIcon = true,
                        hbmMask = hbmMask,
                        hbmColor = hbmColor
                    };
                    return CreateIconIndirect(ref iconInfo);
                }
                finally
                {
                    if (hbmColor != IntPtr.Zero) DeleteObject(hbmColor);
                    if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
                }
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
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

        if (_customIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_customIconHandle);
            _customIconHandle = IntPtr.Zero;
        }

        _hwndSource?.RemoveHook(WndProc);
    }
}
