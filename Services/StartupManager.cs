using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace HandheldDsuServer.Services;

/// <summary>
/// Manages automatic Windows startup registration (HKCU\Software\Microsoft\Windows\CurrentVersion\Run)
/// so the application can launch minimized into the system tray.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "HandheldGyroDSU";

    public static bool IsStartupEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            object? val = key?.GetValue(AppValueName);
            return val is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetStartup(bool enable, string? customExePath = null)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
                return false;

            if (enable)
            {
                string exePath = customExePath ?? ResolvePreferredExecutablePath();
                string command = $"\"{exePath}\" --minimized";
                key.SetValue(AppValueName, command, RegistryValueKind.String);
                return true;
            }
            else
            {
                if (key.GetValue(AppValueName) is not null)
                {
                    key.DeleteValue(AppValueName, throwOnMissingValue: false);
                }
                return false;
            }
        }
        catch
        {
            return IsStartupEnabled();
        }
    }

    private static string ResolvePreferredExecutablePath()
    {
        string currentExe = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "HandheldGyroDSU.exe");

        // If called from the console server binary and HandheldGyroDSU.exe exists alongside, prefer the GUI binary
        if (currentExe.EndsWith("HandheldDsuServer.exe", StringComparison.OrdinalIgnoreCase))
        {
            string baseDir = AppContext.BaseDirectory;
            string candidateWidget = Path.GetFullPath(Path.Combine(
                baseDir, "..", "..", "..", "HandheldDsuWidget", "bin", "Debug", "net10.0-windows10.0.19041.0", "HandheldGyroDSU.exe"));
            if (File.Exists(candidateWidget))
                return candidateWidget;
        }

        return currentExe;
    }
}
