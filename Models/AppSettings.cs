using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace HandheldDsuServer.Models;

/// <summary>
/// Persists user configuration in %LocalAppData%\HandheldDsu\settings.json.
/// </summary>
public sealed class AppSettings
{
    public int UdpPort { get; set; } = 26760;
    public int SecondaryUdpPort { get; set; } = 26762;
    public bool IsSecondaryClusterActive { get; set; }
    public int ActiveSlot { get; set; }
    public string Language { get; set; } = GetDefaultSystemLanguage();
    public string UpdateChannel { get; set; } = "stable";
    public string ActiveProfileName { get; set; } = "Standard Landscape";
    public bool AutoSwitchProfiles { get; set; } = true;
    public bool IsStreamingEnabled { get; set; } = true;
    public bool ForwardGamepadInputs { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; }
    public bool GameBarOverlayEnabled { get; set; } = true;
    public bool HudNotificationsEnabled { get; set; } = true;
    public bool TabletopCalibrationGuard { get; set; } = true;
    public bool InvertX { get; set; } = false;
    public bool InvertY { get; set; } = false;
    public bool InvertZ { get; set; } = false;
    public bool SwapAxes { get; set; } = false;
    public int AxisSchemaVersion { get; set; }
    public bool UsePitchOffset { get; set; } = true;
    public float Sensitivity { get; set; } = 1.0f;
    public float Deadzone { get; set; } = 0.0f;
    public float RestAccX { get; set; }
    public float RestAccY { get; set; }
    public float RestAccZ { get; set; } = -1.0f;
    public float BiasX { get; set; }
    public float BiasY { get; set; }
    public float BiasZ { get; set; }
    public bool GyroMouseEnabled { get; set; }
    public bool GyroMouseAlwaysActive { get; set; }
    public uint GyroMouseTriggerMask { get; set; } = 0x10000;
    public string GyroMouseTriggerDisplay { get; set; } = "LT";
    public float GyroMouseSpeed { get; set; } = 1.2f;

    public static string GetDefaultSystemLanguage()
    {
        string iso = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
        return string.Equals(iso, "tr", StringComparison.OrdinalIgnoreCase) ? "tr" : "en";
    }

    public static string DataDirectoryPath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HandheldDsu");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsFilePath => Path.Combine(DataDirectoryPath, "settings.json");

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
                    if (loaded.SecondaryUdpPort is < 1024 or > 65535 || loaded.SecondaryUdpPort == loaded.UdpPort)
                        loaded.SecondaryUdpPort = 26762;
                    if (string.IsNullOrWhiteSpace(loaded.Language))
                        loaded.Language = GetDefaultSystemLanguage();
                    if (string.IsNullOrWhiteSpace(loaded.UpdateChannel))
                        loaded.UpdateChannel = "stable";
                    if (loaded.Sensitivity <= 0.05f)
                        loaded.Sensitivity = 1.0f;
                    if (loaded.GyroMouseSpeed <= 0.05f)
                        loaded.GyroMouseSpeed = 1.2f;
                    if (loaded.GyroMouseTriggerMask == 0)
                        loaded.GyroMouseTriggerMask = 0x10000;
                    if (string.IsNullOrWhiteSpace(loaded.GyroMouseTriggerDisplay))
                        loaded.GyroMouseTriggerDisplay = "LT";
                    loaded.Deadzone = Math.Clamp(loaded.Deadzone, 0f, 5f);
                    if (loaded.TabletopCalibrationGuard &&
                        (MathF.Abs(loaded.BiasX) > 1.8f || MathF.Abs(loaded.BiasY) > 1.8f || MathF.Abs(loaded.BiasZ) > 1.8f))
                    {
                        loaded.BiasX = Math.Clamp(loaded.BiasX, -0.5f, 0.5f);
                        loaded.BiasY = Math.Clamp(loaded.BiasY, -0.5f, 0.5f);
                        loaded.BiasZ = Math.Clamp(loaded.BiasZ, -0.5f, 0.5f);
                    }
                    if (loaded.AxisSchemaVersion < 2)
                    {
                        if (string.Equals(loaded.ActiveProfileName, "Flight & Racing (Inverted X)", StringComparison.OrdinalIgnoreCase))
                        {
                            loaded.InvertX = true;
                            loaded.InvertY = false;
                            loaded.InvertZ = false;
                            loaded.SwapAxes = false;
                        }
                        else if (string.Equals(loaded.ActiveProfileName, "Portrait Sensor (Legion Go)", StringComparison.OrdinalIgnoreCase))
                        {
                            loaded.InvertX = true;
                            loaded.InvertY = false;
                            loaded.InvertZ = false;
                            loaded.SwapAxes = true;
                        }
                        else
                        {
                            loaded.InvertX = false;
                            loaded.InvertY = false;
                            loaded.InvertZ = false;
                            loaded.SwapAxes = false;
                        }
                        loaded.AxisSchemaVersion = 2;
                        loaded.Save();
                    }
                    if (loaded.AxisSchemaVersion < 3)
                    {
                        loaded.ForwardGamepadInputs = true;
                        loaded.AxisSchemaVersion = 3;
                        loaded.Save();
                    }
                    return loaded;
                }
            }
        }
        catch
        {
            // Return default settings if the configuration file is missing or corrupted
        }

        return new AppSettings { AxisSchemaVersion = 3, ForwardGamepadInputs = true };
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
