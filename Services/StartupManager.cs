using System;
using System.Diagnostics;
using System.IO;
using HandheldDsuServer.Services.Platform;
using Microsoft.Win32;

namespace HandheldDsuServer.Services;

/// <summary>
/// Platform-abstracted automatic startup manager.
/// Dispatches to Windows Registry, macOS LaunchAgents, or Linux XDG Autostart depending on the host OS.
/// </summary>
public static class StartupManager
{
    public static IStartupPlatformBackend Backend { get; } = CreatePlatformBackend();

    private static IStartupPlatformBackend CreatePlatformBackend()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsRegistryStartupBackend();
        if (OperatingSystem.IsMacOS())
            return new MacOsLaunchAgentStartupBackend();
        return new LinuxXdgAutostartBackend();
    }

    public static bool IsStartupEnabled() => Backend.IsStartupEnabled();

    public static bool SetStartup(bool enable, string? customExePath = null) =>
        Backend.SetStartup(enable, customExePath);

    internal static string ResolvePreferredExecutablePath()
    {
        string currentExe = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "HandheldGyroDSU.exe" : "HandheldGyroDSU");

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

/// <summary>
/// Windows implementation of <see cref="IStartupPlatformBackend"/> using HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// </summary>
public sealed class WindowsRegistryStartupBackend : IStartupPlatformBackend
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "HandheldGyroDSU";

    public bool IsStartupEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(AppValueName) is string s && !string.IsNullOrWhiteSpace(s))
            {
                string preferredExe = StartupManager.ResolvePreferredExecutablePath();
                string expectedCommand = $"\"{preferredExe}\" --minimized";
                if (!string.Equals(s, expectedCommand, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(preferredExe))
                {
                    // Auto-heal stale startup registry path (e.g. after executable rename or folder move)
                    key.SetValue(AppValueName, expectedCommand, RegistryValueKind.String);
                }
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public bool SetStartup(bool enable, string? customExePath = null)
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
                string exePath = customExePath ?? StartupManager.ResolvePreferredExecutablePath();
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
}

/// <summary>
/// macOS implementation of <see cref="IStartupPlatformBackend"/> using ~/Library/LaunchAgents plist registration.
/// </summary>
public sealed class MacOsLaunchAgentStartupBackend : IStartupPlatformBackend
{
    private static string GetPlistPath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "LaunchAgents", "com.handheldgyrodsu.app.plist");
    }

    public bool IsStartupEnabled() => OperatingSystem.IsMacOS() && File.Exists(GetPlistPath());

    public bool SetStartup(bool enable, string? customExePath = null)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        try
        {
            string plistPath = GetPlistPath();
            if (enable)
            {
                string exePath = customExePath ?? StartupManager.ResolvePreferredExecutablePath();
                string? dir = Path.GetDirectoryName(plistPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                string plistContent = $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0">
                    <dict>
                        <key>Label</key>
                        <string>com.handheldgyrodsu.app</string>
                        <key>ProgramArguments</key>
                        <array>
                            <string>{exePath}</string>
                            <string>--minimized</string>
                        </array>
                        <key>RunAtLoad</key>
                        <true/>
                    </dict>
                    </plist>
                    """;
                File.WriteAllText(plistPath, plistContent);
                return true;
            }
            else
            {
                if (File.Exists(plistPath))
                    File.Delete(plistPath);
                return false;
            }
        }
        catch
        {
            return IsStartupEnabled();
        }
    }
}

/// <summary>
/// Linux implementation of <see cref="IStartupPlatformBackend"/> using XDG Autostart (~/.config/autostart).
/// </summary>
public sealed class LinuxXdgAutostartBackend : IStartupPlatformBackend
{
    private static string GetDesktopEntryPath()
    {
        string configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(configDir))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            configDir = Path.Combine(home, ".config");
        }
        return Path.Combine(configDir, "autostart", "handheld-gyro-dsu.desktop");
    }

    public bool IsStartupEnabled() => OperatingSystem.IsLinux() && File.Exists(GetDesktopEntryPath());

    public bool SetStartup(bool enable, string? customExePath = null)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            string desktopPath = GetDesktopEntryPath();
            if (enable)
            {
                string exePath = customExePath ?? StartupManager.ResolvePreferredExecutablePath();
                string? dir = Path.GetDirectoryName(desktopPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                string desktopEntry = $"""
                    [Desktop Entry]
                    Type=Application
                    Name=HandheldGyroDSU
                    Comment=250Hz Cemuhook DSU Motion Server & Gyro Companion
                    Exec="{exePath}" --minimized
                    Terminal=false
                    StartupNotify=false
                    """;
                File.WriteAllText(desktopPath, desktopEntry);
                return true;
            }
            else
            {
                if (File.Exists(desktopPath))
                    File.Delete(desktopPath);
                return false;
            }
        }
        catch
        {
            return IsStartupEnabled();
        }
    }
}
