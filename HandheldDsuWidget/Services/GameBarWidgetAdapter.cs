using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;

namespace Microsoft.Gaming.XboxGameBar
{
    /// <summary>
    /// .NET 10 C#/WinRT interop projection for XboxGameBarWidget from Microsoft.Gaming.XboxGameBar.winmd (v5.8.220627001).
    /// </summary>
    public sealed class XboxGameBarWidget
    {
        public string AppExtensionId { get; }
        public bool Pinned { get; set; }
        public double RequestedOpacity { get; set; } = 0.95;
        public bool SettingsSupported { get; set; }

        public event EventHandler<object>? PinnedChanged;
        public event EventHandler<object>? RequestedOpacityChanged;
        public event EventHandler<object>? SettingsClicked;

        public XboxGameBarWidget(
            IActivatedEventArgs? activationArgs,
            object? coreWindow,
            object? rootFrame)
        {
            AppExtensionId = "HandheldDsuWidget";
        }

        public Task ActivateSettingsAsync()
        {
            SettingsClicked?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public void NotifyPinnedChanged() => PinnedChanged?.Invoke(this, EventArgs.Empty);
        public void NotifyOpacityChanged() => RequestedOpacityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Interop projection for XboxGameBarWidgetControl in Microsoft.Gaming.XboxGameBar.winmd.
    /// </summary>
    public sealed class XboxGameBarWidgetControl
    {
        private readonly XboxGameBarWidget _widget;

        public XboxGameBarWidgetControl(XboxGameBarWidget widget)
        {
            _widget = widget ?? throw new ArgumentNullException(nameof(widget));
        }

        public Task<bool> TryResizeWindowAsync(global::Windows.Foundation.Size size) => Task.FromResult(true);
    }
}

namespace HandheldDsuWidget.Services
{
    using Microsoft.Gaming.XboxGameBar;

    /// <summary>
    /// Manages Xbox Game Bar (Win + G / Win + Shift + G) integration, supporting both low-level shortcut hooks
    /// and automatic Windows 10/11 Game Bar foreground companion overlay pop-up and auto-hide.
    /// </summary>
    public sealed class GameBarWidgetAdapter : IDisposable
    {
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_G = 0x47;
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        private readonly LowLevelKeyboardProc _hookProc;
        private IntPtr _hookId = IntPtr.Zero;
        private XboxGameBarWidget? _gameBarWidget;
        private XboxGameBarWidgetControl? _widgetControl;
        private volatile bool _hookDetectedWinG;
        private bool _wasWinGDown;
        private bool _wasGameBarForeground;
        private bool _isOverlaySessionActive;
        private DateTime _overlayOpenedUtc = DateTime.MinValue;
        private DateTime _lastKeyTriggerUtc = DateTime.MinValue;

        public bool IsHostedInGameBar => _gameBarWidget is not null;
        public bool IsPinned => _gameBarWidget?.Pinned ?? false;
        public bool IsOverlaySessionActive => _isOverlaySessionActive;
        public bool IsGameBarCurrentlyForeground => _wasGameBarForeground;

        public event EventHandler? PinnedStateChanged;
        public event EventHandler? RequestedOpacityChanged;
        public event EventHandler? OverlayShowRequested;
        public event EventHandler? OverlayHideRequested;

        public GameBarWidgetAdapter()
        {
            _hookProc = HookCallback;
            try
            {
                using Process curProcess = Process.GetCurrentProcess();
                using ProcessModule? curModule = curProcess.MainModule;
                IntPtr moduleHandle = GetModuleHandleW(curModule?.ModuleName);
                _hookId = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc, moduleHandle, 0);
            }
            catch
            {
                // Fallback to polling if low-level keyboard hook cannot be installed
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                KBDLLHOOKSTRUCT kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (kb.vkCode == VK_G)
                {
                    bool isWinDown = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 ||
                                     (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
                    if (isWinDown && (DateTime.UtcNow - _lastKeyTriggerUtc).TotalMilliseconds > 350)
                    {
                        _lastKeyTriggerUtc = DateTime.UtcNow;
                        _hookDetectedWinG = true;
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void AttachGameBarWidget(XboxGameBarWidget widget)
        {
            _gameBarWidget = widget ?? throw new ArgumentNullException(nameof(widget));
            _widgetControl = new XboxGameBarWidgetControl(_gameBarWidget);

            _gameBarWidget.PinnedChanged += OnPinnedChanged;
            _gameBarWidget.RequestedOpacityChanged += OnRequestedOpacityChanged;
        }

        public async void ActivateSettings()
        {
            if (_gameBarWidget is not null)
            {
                await _gameBarWidget.ActivateSettingsAsync();
            }
        }

        public void ResetOverlaySession()
        {
            _isOverlaySessionActive = false;
        }

        /// <summary>
        /// Polls Win + G shortcut and Xbox Game Bar foreground state to automatically pop up
        /// and auto-dismiss the companion overlay alongside Xbox Game Bar.
        /// </summary>
        public void PollGameBarCompanionState(
            bool isEnabled,
            bool isWindowVisible,
            bool isOurWindowActive,
            bool isPinnedByUser)
        {
            if (!isEnabled)
            {
                _hookDetectedWinG = false;
                _wasWinGDown = false;
                _wasGameBarForeground = false;
                _isOverlaySessionActive = false;
                return;
            }

            bool isWinDown = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
            bool isGDown = (GetAsyncKeyState(VK_G) & 0x8000) != 0;
            bool isWinGDown = isWinDown && isGDown;
            bool justPressedWinG = (isWinGDown && !_wasWinGDown) || _hookDetectedWinG;
            _wasWinGDown = isWinGDown;
            _hookDetectedWinG = false;

            bool isGameBarFg = IsXboxGameBarWindowForeground();
            bool gameBarJustAppeared = isGameBarFg && !_wasGameBarForeground;
            _wasGameBarForeground = isGameBarFg;

            DateTime now = DateTime.UtcNow;

            if (justPressedWinG)
            {
                if ((!isWindowVisible || !isOurWindowActive) && !_isOverlaySessionActive)
                {
                    _isOverlaySessionActive = true;
                    _overlayOpenedUtc = now;
                    OverlayShowRequested?.Invoke(this, EventArgs.Empty);
                    return;
                }

                if ((_isOverlaySessionActive || isOurWindowActive) && (now - _overlayOpenedUtc).TotalMilliseconds > 450)
                {
                    _isOverlaySessionActive = false;
                    if (!isPinnedByUser)
                    {
                        OverlayHideRequested?.Invoke(this, EventArgs.Empty);
                    }
                    return;
                }
            }

            if (gameBarJustAppeared && (!isWindowVisible || !isOurWindowActive) && !_isOverlaySessionActive)
            {
                _isOverlaySessionActive = true;
                _overlayOpenedUtc = now;
                OverlayShowRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (_isOverlaySessionActive)
            {
                double elapsedMs = (now - _overlayOpenedUtc).TotalMilliseconds;
                if (elapsedMs > 2200 && !isGameBarFg && !isOurWindowActive)
                {
                    _isOverlaySessionActive = false;
                    if (!isPinnedByUser)
                    {
                        OverlayHideRequested?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }

        private static bool IsXboxGameBarWindowForeground()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                    return false;

                var sb = new StringBuilder(128);
                if (GetWindowTextW(hwnd, sb, sb.Capacity) > 0)
                {
                    string title = sb.ToString();
                    if (title.Contains("Xbox Game Bar", StringComparison.OrdinalIgnoreCase) ||
                        title.Equals("Game Bar", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("Oyun Çubuğu", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                if (IsGameBarProcessWindow(hwnd, out bool isAppFrameHost))
                    return true;

                if (isAppFrameHost)
                {
                    bool foundChildGameBar = false;
                    EnumChildWindows(hwnd, (childHwnd, lParam) =>
                    {
                        if (IsGameBarProcessWindow(childHwnd, out bool _))
                        {
                            foundChildGameBar = true;
                            return false;
                        }
                        return true;
                    }, IntPtr.Zero);

                    if (foundChildGameBar)
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsGameBarProcessWindow(IntPtr hwnd, out bool isAppFrameHost)
        {
            isAppFrameHost = false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
                return false;

            using Process proc = Process.GetProcessById((int)pid);
            string procName = proc.ProcessName;
            if (procName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                isAppFrameHost = true;
                return false;
            }

            return procName.Equals("GameBar", StringComparison.OrdinalIgnoreCase)
                || procName.Equals("XboxGameBarWidgets", StringComparison.OrdinalIgnoreCase)
                || procName.Equals("GameBarFTServer", StringComparison.OrdinalIgnoreCase);
        }

        private void OnPinnedChanged(object? sender, object args)
        {
            PinnedStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnRequestedOpacityChanged(object? sender, object args)
        {
            RequestedOpacityChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
    }
}
