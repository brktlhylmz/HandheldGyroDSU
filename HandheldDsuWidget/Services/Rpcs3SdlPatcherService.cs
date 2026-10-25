using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace HandheldDsuWidget.Services;

public enum Rpcs3PatchState
{
    NotFound,
    Unpatched,
    Patched,
    UnsupportedBinary
}

/// <summary>
/// Inspects, patches, and restores RPCS3's SDL3 initialization flags (adding SDL_INIT_SENSOR | SDL_INIT_VIDEO = 0x1B020)
/// so Windows handhelds (such as ASUS ROG Ally) stream native 6-axis IMU motion in RPCS3 with proper landscape axes
/// without requiring virtual controller drivers or HidHide.
/// </summary>
public static class Rpcs3SdlPatcherService
{
    private static string? _customExePath;

    public static string? CustomExePath
    {
        get => _customExePath;
        set => _customExePath = value;
    }

    public static string? DiscoverRpcs3ExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_customExePath) && File.Exists(_customExePath))
        {
            return _customExePath;
        }

        try
        {
            foreach (Process p in Process.GetProcessesByName("rpcs3"))
            {
                using (p)
                {
                    string? runningPath = p.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(runningPath) && File.Exists(runningPath))
                    {
                        _customExePath = runningPath;
                        return runningPath;
                    }
                }
            }
        }
        catch
        {
            // Ignore access errors when querying processes
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string[] candidates =
        [
            Path.Combine(appData, "emudeck", "Emulators", "rpcs3", "rpcs3.exe"),
            Path.Combine(appData, "rpcs3", "rpcs3.exe"),
            Path.Combine(localAppData, "rpcs3", "rpcs3.exe"),
            Path.Combine(userProfile, "Downloads", "rpcs3", "rpcs3.exe"),
            @"C:\Emulation\Emulators\rpcs3\rpcs3.exe",
            @"D:\Emulation\Emulators\rpcs3\rpcs3.exe"
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static (Rpcs3PatchState State, string? ExePath, bool HasBackup) GetCurrentStatus()
    {
        string? exePath = DiscoverRpcs3ExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return (Rpcs3PatchState.NotFound, null, false);
        }

        bool hasBackup = File.Exists(exePath + ".bak");

        try
        {
            byte[] bytes = ReadFileBytesShared(exePath);
            int offset = FindSdlInitInstructionOffset(bytes, out bool isFullyPatched);
            if (offset < 0)
            {
                return (Rpcs3PatchState.UnsupportedBinary, exePath, hasBackup);
            }

            return (isFullyPatched ? Rpcs3PatchState.Patched : Rpcs3PatchState.Unpatched, exePath, hasBackup);
        }
        catch
        {
            return (Rpcs3PatchState.UnsupportedBinary, exePath, hasBackup);
        }
    }

    public static bool ApplyPatch(string exePath, out string errorMessage)
    {
        errorMessage = string.Empty;

        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                errorMessage = "rpcs3.exe not found.";
                return false;
            }

            StopRunningRpcs3Processes();

            byte[] bytes = File.ReadAllBytes(exePath);
            int offset = FindSdlInitInstructionOffset(bytes, out bool isFullyPatched);
            if (offset < 0)
            {
                errorMessage = "SDL_Init signature not found in rpcs3.exe.";
                return false;
            }

            string backupPath = exePath + ".bak";
            bool isCleanUnpatched = bytes[offset + 1] == 0x00 && bytes[offset + 2] == 0x30;
            if (isCleanUnpatched || !File.Exists(backupPath))
            {
                File.Copy(exePath, backupPath, overwrite: true);
            }

            if (!isFullyPatched)
            {
                using FileStream fs = new(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                fs.Position = offset + 1;
                // Patch mov ecx, 0x00013000 (B9 00 30 01 00) -> mov ecx, 0x0001B020 (B9 20 B0 01 00)
                // 0x0001B020 = SDL_INIT_VIDEO (0x20) | SDL_INIT_HAPTIC (0x1000) | SDL_INIT_GAMEPAD (0x2000) | SDL_INIT_SENSOR (0x8000) | SDL_INIT_CAMERA (0x10000)
                fs.WriteByte(0x20);
                fs.WriteByte(0xB0);
                fs.Flush();
            }

            EnsureRpcs3SdlPadConfig(exePath);
            _customExePath = exePath;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public static bool RestoreOriginal(string exePath, out string errorMessage)
    {
        errorMessage = string.Empty;

        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                errorMessage = "rpcs3.exe not found.";
                return false;
            }

            StopRunningRpcs3Processes();

            string backupPath = exePath + ".bak";
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, exePath, overwrite: true);
                return true;
            }

            byte[] bytes = File.ReadAllBytes(exePath);
            int offset = FindSdlInitInstructionOffset(bytes, out _);
            if (offset < 0)
            {
                errorMessage = "Backup file not found and signature could not be located.";
                return false;
            }

            using FileStream fs = new(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            fs.Position = offset + 1;
            fs.WriteByte(0x00);
            fs.WriteByte(0x30);
            fs.Flush();
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static int FindSdlInitInstructionOffset(byte[] bytes, out bool isFullyPatched)
    {
        isFullyPatched = false;
        int limit = bytes.Length - 12;

        for (int i = 0; i < limit; i++)
        {
            // Look for: B9 (00|20) (30|B0) 01 00 followed by CALL (FF 15 rel32 or E8 rel32)
            if (bytes[i] == 0xB9 &&
                (bytes[i + 1] == 0x00 || bytes[i + 1] == 0x20) &&
                (bytes[i + 2] == 0x30 || bytes[i + 2] == 0xB0) &&
                bytes[i + 3] == 0x01 &&
                bytes[i + 4] == 0x00)
            {
                bool followedByCall =
                    (bytes[i + 5] == 0xFF && bytes[i + 6] == 0x15) ||
                    bytes[i + 5] == 0xE8;

                if (followedByCall)
                {
                    isFullyPatched = bytes[i + 1] == 0x20 && bytes[i + 2] == 0xB0;
                    return i;
                }
            }
        }

        return -1;
    }

    private static byte[] ReadFileBytesShared(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] buffer = new byte[fs.Length];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = fs.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read <= 0)
                break;
            totalRead += read;
        }
        return buffer;
    }

    private static void StopRunningRpcs3Processes()
    {
        try
        {
            Process[] procs = Process.GetProcessesByName("rpcs3");
            if (procs.Length == 0)
                return;

            foreach (Process p in procs)
            {
                using (p)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(2000);
                    }
                    catch
                    {
                        // Ignore process termination errors
                    }
                }
            }

            Thread.Sleep(250);
        }
        catch
        {
            // Ignore process enumeration errors
        }
    }

    private static void EnsureRpcs3SdlPadConfig(string exePath)
    {
        try
        {
            string? exeDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(exeDir))
                return;

            string defaultYmlPath = Path.Combine(exeDir, "config", "input_configs", "global", "Default.yml");
            if (!File.Exists(defaultYmlPath))
                return;

            string yml = File.ReadAllText(defaultYmlPath);
            string updated = Regex.Replace(
                yml,
                @"(Player 1 Input:\r?\n\s*Handler:\s*)XInput(\r?\n\s*Device:\s*)[^\r\n]+",
                "${1}SDL${2}XInput Controller 1");

            if (!string.Equals(yml, updated, StringComparison.Ordinal))
            {
                File.WriteAllText(defaultYmlPath, updated);
            }
        }
        catch
        {
            // Ignore config update errors
        }
    }
}
