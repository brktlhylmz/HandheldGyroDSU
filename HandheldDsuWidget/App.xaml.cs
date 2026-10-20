using System;
using System.IO;
using System.Linq;
using System.Windows;
using HandheldDsuServer.Models;

namespace HandheldDsuWidget;

public partial class App : Application
{
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
            bool startMinimized = e.Args.Any(a =>
                string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-m", StringComparison.OrdinalIgnoreCase)) ||
                AppSettings.Load().StartMinimized;

            var window = new MainWindow(startMinimized);
            MainWindow = window;

            if (startMinimized)
            {
                // Initialize HWND for system tray hook and background DSU engine, then hide to tray
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Opacity = 0;
                window.Show();
                window.HideToTray();
                window.Opacity = 1;
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
}
