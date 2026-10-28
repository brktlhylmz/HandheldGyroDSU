using System;
using System.Text.Json.Serialization;

namespace HandheldDsuServer.Models;

/// <summary>
/// Represents a shareable hardware profile (.json) containing axis inversions,
/// sensor orientation mapping, and zero pitch compensation settings.
/// </summary>
public sealed class HandheldProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Default";

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("invertX")]
    public bool InvertX { get; set; }

    [JsonPropertyName("invertY")]
    public bool InvertY { get; set; }

    [JsonPropertyName("invertZ")]
    public bool InvertZ { get; set; }

    [JsonPropertyName("swapAxes")]
    public bool SwapAxes { get; set; }

    [JsonPropertyName("usePitchOffset")]
    public bool UsePitchOffset { get; set; } = true;

    [JsonPropertyName("sensitivity")]
    public float Sensitivity { get; set; } = 1.0f;

    [JsonPropertyName("deadzone")]
    public float Deadzone { get; set; } = 0.0f;

    [JsonPropertyName("preferredSlot")]
    public int PreferredSlot { get; set; }

    [JsonPropertyName("linkedProcess")]
    public string? LinkedProcess { get; set; }

    [JsonPropertyName("recenterHotkey")]
    public string? RecenterHotkey { get; set; }

    [JsonPropertyName("recenterHotkeyDisplay")]
    public string? RecenterHotkeyDisplay { get; set; }

    [JsonPropertyName("gyroMouseEnabled")]
    public bool GyroMouseEnabled { get; set; }

    [JsonPropertyName("gyroMouseAlwaysActive")]
    public bool GyroMouseAlwaysActive { get; set; }

    [JsonPropertyName("gyroMouseTriggerMask")]
    public uint GyroMouseTriggerMask { get; set; } = 0x10000;

    [JsonPropertyName("gyroMouseTriggerDisplay")]
    public string GyroMouseTriggerDisplay { get; set; } = "LT";

    [JsonPropertyName("gyroMouseSpeed")]
    public float GyroMouseSpeed { get; set; } = 1.2f;
}
