using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HandheldDsuServer.Models;

/// <summary>
/// Represents a remembered physical controller (identified by VID:PID) and its bound profile.
/// </summary>
public sealed class KnownControllerConfig
{
    [JsonPropertyName("hardwareId")]
    public string HardwareId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("assignedProfileName")]
    public string AssignedProfileName { get; set; } = "Standard Landscape";

    [JsonPropertyName("autoSwitchProfile")]
    public bool AutoSwitchProfile { get; set; } = true;

    [JsonPropertyName("lastSeenUtc")]
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsConnected { get; set; }

    [JsonIgnore]
    public int AssignedPlayerIndex { get; set; }

    [JsonIgnore]
    public int AssignedUdpPort { get; set; } = 26760;

    [JsonIgnore]
    public int AssignedDsuSlot { get; set; }
}

/// <summary>
/// Represents an automatic profile rule triggered when a target emulator or game (.exe) is active.
/// </summary>
public sealed class EmulatorProfileRule
{
    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("profileName")]
    public string ProfileName { get; set; } = "Standard Landscape";

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }
}

/// <summary>
/// Persists remembered controllers and emulator auto-switch rules in %LocalAppData%\HandheldDsu\automation.json.
/// </summary>
public sealed class AutomationConfig
{
    [JsonPropertyName("autoSwitchByController")]
    public bool AutoSwitchByController { get; set; } = true;

    [JsonPropertyName("autoSwitchByEmulator")]
    public bool AutoSwitchByEmulator { get; set; } = true;

    [JsonPropertyName("controllers")]
    public List<KnownControllerConfig> Controllers { get; set; } = [];

    [JsonPropertyName("emulatorRules")]
    public List<EmulatorProfileRule> EmulatorRules { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string FilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HandheldDsu");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "automation.json");
        }
    }

    public static AutomationConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                AutomationConfig? loaded = JsonSerializer.Deserialize<AutomationConfig>(json, JsonOptions);
                if (loaded is not null)
                {
                    EnsureDefaultEmulatorRules(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // Fallback to default config
        }

        var config = new AutomationConfig();
        EnsureDefaultEmulatorRules(config);
        config.Save();
        return config;
    }

    private static void EnsureDefaultEmulatorRules(AutomationConfig config)
    {
        if (config.EmulatorRules.Count > 0)
            return;

        config.EmulatorRules.AddRange(
        [
            new EmulatorProfileRule { ProcessName = "eden", DisplayName = "Eden (Switch)", ProfileName = "Standard Landscape", IsEnabled = false },
            new EmulatorProfileRule { ProcessName = "yuzu", DisplayName = "Yuzu (Switch)", ProfileName = "Standard Landscape", IsEnabled = false },
            new EmulatorProfileRule { ProcessName = "cemu", DisplayName = "Cemu (Wii U)", ProfileName = "Standard Landscape", IsEnabled = false },
            new EmulatorProfileRule { ProcessName = "ryujinx", DisplayName = "Ryujinx (Switch)", ProfileName = "Standard Landscape", IsEnabled = false },
            new EmulatorProfileRule { ProcessName = "dolphin", DisplayName = "Dolphin (Wii / GC)", ProfileName = "Standard Landscape", IsEnabled = false }
        ]);
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }
}
