using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HandheldDsuServer.Models;

namespace HandheldDsuWidget;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\HandheldGyroDsu_SingleInstanceMutex";
    private const string ActivateWindowEventName = @"Local\HandheldGyroDsu_ActivateEvent";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "widget_error.log"),
                    args.Exception.ToString());
            }
            catch
            {
                // Ignore logging failures
            }
        };

        try
        {
            _instanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
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
                window.ShowInTaskbar = false;
                window.WindowState = WindowState.Minimized;
                window.Show();
                window.HideToTray();
            }
            else
            {
                window.Show();
                window.Activate();
                window.Topmost = true;
                window.Focus();
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "widget_error.log"),
                ex.ToString());
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _activateEvent?.Dispose();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch
        {
            // Ignore cleanup errors
        }

        base.OnExit(e);
    }
}
