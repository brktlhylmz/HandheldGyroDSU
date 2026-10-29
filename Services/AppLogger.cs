using System;
using System.IO;
using HandheldDsuServer.Models;

namespace HandheldDsuServer.Services;

/// <summary>
/// Lightweight, thread-safe operational file logger writing to %LocalAppData%\HandheldDsu\app.log.
/// Automatically rotates the log file when it exceeds 2 MB to prevent disk exhaustion.
/// </summary>
public static class AppLogger
{
    private static readonly object LogLock = new();
    private const long MaxLogSizeBytes = 2 * 1024 * 1024; // 2 MB

    public static string LogFilePath => Path.Combine(AppSettings.DataDirectoryPath, "app.log");

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message, Exception? ex = null)
    {
        string fullMessage = ex is not null ? $"{message} -> {ex}" : message;
        Log("ERROR", fullMessage);
    }

    private static void Log(string level, string message)
    {
        try
        {
            lock (LogLock)
            {
                string path = LogFilePath;
                FileInfo fi = new(path);
                if (fi.Exists && fi.Length > MaxLogSizeBytes)
                {
                    string oldPath = Path.Combine(AppSettings.DataDirectoryPath, "app.log.old");
                    try
                    {
                        if (File.Exists(oldPath))
                        {
                            File.Delete(oldPath);
                        }
                        File.Move(path, oldPath);
                    }
                    catch
                    {
                        // Fall through if rotation fails
                    }
                }

                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(path, entry);
            }
        }
        catch
        {
            // Logging must never crash the host process
        }
    }
}
