using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace HandheldDsuServer.Services;

/// <summary>
/// Windows başlangıcında (HKCU\Software\Microsoft\Windows\CurrentVersion\Run)
/// uygulamanın sistem tepsisinde (gizli / arka plan modunda) otomatik başlatılmasını yönetir.
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
            ?? Path.Combine(AppContext.BaseDirectory, "HandheldDsuWidget.exe");

        // Eğer şu an konsol sunucusu içindeysek ve yan klasörde HandheldDsuWidget.exe varsa onu tercih et
        if (currentExe.EndsWith("HandheldDsuServer.exe", StringComparison.OrdinalIgnoreCase))
        {
            string baseDir = AppContext.BaseDirectory;
            string candidateWidget = Path.GetFullPath(Path.Combine(
                baseDir, "..", "..", "..", "HandheldDsuWidget", "bin", "Debug", "net10.0-windows10.0.19041.0", "HandheldDsuWidget.exe"));
            if (File.Exists(candidateWidget))
                return candidateWidget;
        }

        return currentExe;
    }
}
