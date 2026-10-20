using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HandheldDsuServer.Models;

namespace HandheldDsuServer.Services;

/// <summary>
/// Manages user and community profile JSON files in %LocalAppData%\HandheldDsu\Profiles.
/// </summary>
public static class ProfileManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ProfilesDirectory
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HandheldDsu",
                "Profiles");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void EnsureDefaultProfiles()
    {
        try
        {
            string dir = ProfilesDirectory;
            if (Directory.GetFiles(dir, "*.json").Length > 0)
                return;

            // Seed built-in starter templates for major handheld configurations
            SaveProfile(new HandheldProfile
            {
                Name = "Standard Landscape",
                Author = "System",
                Description = "Default orientation for ASUS ROG Ally, MSI Claw, and standard horizontal devices",
                InvertX = false,
                InvertY = true,
                InvertZ = true,
                SwapAxes = false,
                UsePitchOffset = true
            });

            SaveProfile(new HandheldProfile
            {
                Name = "Portrait Sensor (Legion Go)",
                Author = "System",
                Description = "Optimized for portrait-native sensor mounting (Lenovo Legion Go)",
                InvertX = true,
                InvertY = false,
                InvertZ = true,
                SwapAxes = true,
                UsePitchOffset = true
            });

            SaveProfile(new HandheldProfile
            {
                Name = "Flight & Racing (Inverted X)",
                Author = "System",
                Description = "Inverted pitch axis for flight simulator / flight yoke controls",
                InvertX = true,
                InvertY = true,
                InvertZ = true,
                SwapAxes = false,
                UsePitchOffset = true
            });
        }
        catch
        {
            // Ignore seeding errors
        }
    }

    public static IReadOnlyList<HandheldProfile> LoadAllProfiles()
    {
        EnsureDefaultProfiles();
        var list = new List<HandheldProfile>();
        try
        {
            foreach (string file in Directory.GetFiles(ProfilesDirectory, "*.json").OrderBy(f => f))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    HandheldProfile? profile = JsonSerializer.Deserialize<HandheldProfile>(json, JsonOptions);
                    if (profile is not null)
                    {
                        if (string.IsNullOrWhiteSpace(profile.Name))
                        {
                            profile.Name = Path.GetFileNameWithoutExtension(file);
                        }
                        if (profile.Sensitivity <= 0.05f)
                        {
                            profile.Sensitivity = 1.0f;
                        }
                        profile.Deadzone = Math.Clamp(profile.Deadzone, 0f, 5f);
                        list.Add(profile);
                    }
                }
                catch
                {
                    // Ignore corrupted profile file
                }
            }
        }
        catch
        {
            // Ignore directory read errors
        }

        if (list.Count == 0)
        {
            list.Add(new HandheldProfile { Name = "Standard Landscape" });
        }

        return list;
    }

    public static bool SaveProfile(HandheldProfile profile)
    {
        try
        {
            string safeName = SanitizeFileName(profile.Name);
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "Custom Profile";

            profile.Name = safeName;
            string filePath = Path.Combine(ProfilesDirectory, $"{safeName}.json");
            string json = JsonSerializer.Serialize(profile, JsonOptions);
            File.WriteAllText(filePath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool DeleteProfile(string profileName)
    {
        try
        {
            string safeName = SanitizeFileName(profileName);
            string filePath = Path.Combine(ProfilesDirectory, $"{safeName}.json");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                return true;
            }
        }
        catch
        {
            // Ignore delete error
        }
        return false;
    }

    public static string SerializeProfile(HandheldProfile profile)
    {
        return JsonSerializer.Serialize(profile, JsonOptions);
    }

    public static HandheldProfile? DeserializeProfile(string jsonContent)
    {
        try
        {
            HandheldProfile? profile = JsonSerializer.Deserialize<HandheldProfile>(jsonContent, JsonOptions);
            if (profile is not null)
            {
                if (string.IsNullOrWhiteSpace(profile.Name))
                {
                    profile.Name = "Imported Profile";
                }
                if (profile.Sensitivity <= 0.05f)
                {
                    profile.Sensitivity = 1.0f;
                }
                profile.Deadzone = Math.Clamp(profile.Deadzone, 0f, 5f);
            }
            return profile;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = new(name.Where(c => !invalid.Contains(c)).ToArray());
        return clean.Trim();
    }
}
