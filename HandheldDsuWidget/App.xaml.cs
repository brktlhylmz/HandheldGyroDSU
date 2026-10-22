using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using HandheldDsuServer.Models;

namespace HandheldDsuWidget;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\HandheldGyroDsu_SingleInstanceMutex";
    private const string ActivateWindowEventName = @"Local\HandheldGyroDsu_ActivateEvent";
    private const int UOI_NAME = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(
        IntPtr hObj,
        int nIndex,
        StringBuilder pvInfo,
        int nLength,
        out int lpnLengthNeeded);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (TryRelaunchOnInteractiveDefaultDesktop(e.Args))
        {
            Shutdown(0);
            return;
        }

        // Use software rendering for the lightweight utility UI to prevent third-party game overlays
        // (Steam gameoverlayrenderer64.dll, AMD Adrenalin, Armoury Crate) from hooking a D3D9 swapchain
        // and causing 0xc0000005 access violations when switching between emulators.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrashException("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LogCrashException("AppDomainUnhandledException", ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrashException("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        try
        {
            _instanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
            _ownsMutex = createdNew;
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateWindowEventName);

            if (!createdNew)
            {
                // Another instance is already running (e.g. in the system tray) -> wake it up and exit
                _activateEvent.Set();
                Shutdown(0);
                return;
            }

            bool startMinimized = e.Args.Any(a =>
                string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-m", StringComparison.OrdinalIgnoreCase));

            var window = new MainWindow(startMinimized);
            MainWindow = window;

            // Listen for activation signals from subsequent launches to restore the window from tray
            _ = Task.Run(() =>
            {
                while (_activateEvent.WaitOne())
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindow is MainWindow mw)
                        {
                            mw.ShowFromTray();
                        }
                    });
                }
            });

            if (startMinimized)
            {
                // Start minimized without flashing the normal window frame on the desktop
                window.ShowActivated = false;
                window.WindowState = WindowState.Minimized;
                window.Show();
                window.HideToTray();
            }
            else
            {
                window.Show();
                window.Activate();
                window.Focus();
            }
        }
        catch (Exception ex)
        {
            LogCrashException("OnStartup", ex);
            Shutdown(1);
        }
    }

    private static void LogCrashException(string source, Exception ex)
    {
        try
        {
            string logDir = AppSettings.DataDirectoryPath;
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "crash.log");
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(logPath, entry);
        }
        catch
        {
            // Ignore logging failures
        }
    }

    /// <summary>
    /// If launched from an isolated non-interactive desktop (such as an automation sandbox desktop),
    /// re-spawns the process onto the active user desktop (winsta0\default) so the WPF window is visible.
    /// </summary>
    private static bool TryRelaunchOnInteractiveDefaultDesktop(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--default-desktop", StringComparison.OrdinalIgnoreCase)))
            return false;

        try
        {
            IntPtr hDesk = GetThreadDesktop(GetCurrentThreadId());
            if (hDesk == IntPtr.Zero)
                return false;

            var sb = new StringBuilder(128);
            if (!GetUserObjectInformationW(hDesk, UOI_NAME, sb, sb.Capacity * 2, out _))
                return false;

            string desktopName = sb.ToString();
            if (string.IsNullOrWhiteSpace(desktopName) ||
                string.Equals(desktopName, "Default", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return false;

            string joinedArgs = string.Join(" ", args.Select(a => $"\"{a}\""));
            var cmdLine = new StringBuilder($"\"{exePath}\" --default-desktop {joinedArgs}".Trim());

            var si = new STARTUPINFOW
            {
                cb = Marshal.SizeOf<STARTUPINFOW>(),
                lpDesktop = @"winsta0\default"
            };

            if (CreateProcessW(
                exePath,
                cmdLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                0,
                IntPtr.Zero,
                AppContext.BaseDirectory,
                ref si,
                out PROCESS_INFORMATION pi))
            {
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                return true;
            }
        }
        catch
        {
            // Fall through and run in current desktop if relaunch fails
        }

        return false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _activateEvent?.Dispose();
            if (_ownsMutex)
            {
                _instanceMutex?.ReleaseMutex();
            }
            _instanceMutex?.Dispose();
        }
        catch
        {
            // Ignore cleanup errors
        }

        base.OnExit(e);
    }
}
