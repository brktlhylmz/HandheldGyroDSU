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
    public bool InvertY { get; set; } = true;

    [JsonPropertyName("invertZ")]
    public bool InvertZ { get; set; } = true;

    [JsonPropertyName("swapAxes")]
    public bool SwapAxes { get; set; }

    [JsonPropertyName("usePitchOffset")]
    public bool UsePitchOffset { get; set; } = true;

    [JsonPropertyName("preferredSlot")]
    public int PreferredSlot { get; set; }

    [JsonPropertyName("linkedProcess")]
    public string? LinkedProcess { get; set; }
}
