using System;
using System.IO;
using System.Text.Json;

namespace HandheldDsuServer.Models;

/// <summary>
/// Persists user configuration in %LocalAppData%\HandheldDsu\settings.json.
/// </summary>
public sealed class AppSettings
{
    public int UdpPort { get; set; } = 26760;
    public int ActiveSlot { get; set; }
    public string Language { get; set; } = "tr";
    public string ActiveProfileName { get; set; } = "Standard Landscape";
    public bool AutoSwitchProfiles { get; set; } = true;
    public bool IsStreamingEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool InvertX { get; set; } = false;
    public bool InvertY { get; set; } = true;
    public bool InvertZ { get; set; } = true;
    public bool SwapAxes { get; set; } = false;
    public bool UsePitchOffset { get; set; } = true;
    public float RestAccY { get; set; }
    public float RestAccZ { get; set; } = -1.0f;
    public float BiasX { get; set; }
    public float BiasY { get; set; }
    public float BiasZ { get; set; }

    public static string SettingsFilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HandheldDsu");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            string path = SettingsFilePath;
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                {
                    if (loaded.UdpPort is < 1024 or > 65535)
                        loaded.UdpPort = 26760;
                    if (string.IsNullOrWhiteSpace(loaded.Language))
                        loaded.Language = "tr";
                    return loaded;
                }
            }
        }
        catch
        {
            // Return default settings if the configuration file is missing or corrupted
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Ignore transient disk write failures
        }
    }
}
