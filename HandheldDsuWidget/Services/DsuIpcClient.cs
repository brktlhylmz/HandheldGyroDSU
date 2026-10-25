using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HandheldDsuServer.Models;
using HandheldDsuServer.Services;

namespace HandheldDsuWidget.Services;

public sealed class DsuStatusDto
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    [JsonPropertyName("activeProfileName")]
    public string ActiveProfileName { get; set; } = "Standard Landscape";

    [JsonPropertyName("serverPort")]
    public int ServerPort { get; set; } = 26760;

    [JsonPropertyName("activeSlot")]
    public int ActiveSlot { get; set; }

    [JsonPropertyName("wasPortFallbackTriggered")]
    public bool WasPortFallbackTriggered { get; set; }

    [JsonPropertyName("isSecondaryClusterActive")]
    public bool IsSecondaryClusterActive { get; set; }

    [JsonPropertyName("secondaryPort")]
    public int SecondaryPort { get; set; } = 26762;

    [JsonPropertyName("ipcPort")]
    public int IpcPort { get; set; } = 26761;

    [JsonPropertyName("isStreamingEnabled")]
    public bool IsStreamingEnabled { get; set; } = true;

    [JsonPropertyName("forwardGamepadInputs")]
    public bool ForwardGamepadInputs { get; set; }

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    [JsonPropertyName("closeToTray")]
    public bool CloseToTray { get; set; }

    [JsonPropertyName("gameBarOverlayEnabled")]
    public bool GameBarOverlayEnabled { get; set; } = true;

    [JsonPropertyName("hudNotificationsEnabled")]
    public bool HudNotificationsEnabled { get; set; } = true;

    [JsonPropertyName("tabletopCalibrationGuard")]
    public bool TabletopCalibrationGuard { get; set; } = true;

    [JsonPropertyName("isSyntheticWaveEnabled")]
    public bool IsSyntheticWaveEnabled { get; set; }

    [JsonPropertyName("isEmulatorConnected")]
    public bool IsEmulatorConnected { get; set; }

    [JsonPropertyName("connectedClients")]
    public string[] ConnectedClients { get; set; } = [];

    [JsonPropertyName("totalPacketsSent")]
    public long TotalPacketsSent { get; set; }

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

    [JsonPropertyName("sensitivity")]
    public double Sensitivity { get; set; } = 1.0;

    [JsonPropertyName("deadzone")]
    public double Deadzone { get; set; } = 0.0;

    [JsonPropertyName("isCalibrating")]
    public bool IsCalibrating { get; set; }

    [JsonPropertyName("calibrationProgress")]
    public int CalibrationProgress { get; set; } = 100;

    [JsonPropertyName("gyroPitch")]
    public double GyroPitch { get; set; }

    [JsonPropertyName("gyroYaw")]
    public double GyroYaw { get; set; }

    [JsonPropertyName("gyroRoll")]
    public double GyroRoll { get; set; }

    [JsonPropertyName("accX")]
    public double AccX { get; set; }

    [JsonPropertyName("accY")]
    public double AccY { get; set; }

    [JsonPropertyName("accZ")]
    public double AccZ { get; set; }

    [JsonPropertyName("biasX")]
    public double BiasX { get; set; }

    [JsonPropertyName("biasY")]
    public double BiasY { get; set; }

    [JsonPropertyName("biasZ")]
    public double BiasZ { get; set; }
}

/// <summary>
/// Communicates with the background DSU engine (http://127.0.0.1:26761).
/// Automatically starts an in-process embedded DSU engine using settings.json if no external server is running.
/// </summary>
public sealed class DsuIpcClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly object _engineLock = new();

    private CancellationTokenSource? _embeddedCts;
    private AppSettings? _embeddedSettings;
    private SensorService? _embeddedSensor;
    private DsuUdpServer? _embeddedUdpServer;
    private IpcHttpServer? _embeddedIpcServer;
    private bool _usingEmbeddedEngine;

    public DsuIpcClient(string baseUrl = "http://127.0.0.1:26761")
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMilliseconds(400)
        };
    }

    private void EnsureEmbeddedEngineStarted()
    {
        if (_usingEmbeddedEngine && _embeddedSensor is not null && _embeddedUdpServer is not null)
            return;

        lock (_engineLock)
        {
            if (_usingEmbeddedEngine && _embeddedSensor is not null && _embeddedUdpServer is not null)
                return;

            _embeddedCts = new CancellationTokenSource();
            _embeddedSettings = AppSettings.Load();
            _embeddedSettings.StartWithWindows = StartupManager.IsStartupEnabled();

            _embeddedSensor = new SensorService(DeviceProfile.CreateDefault(), _embeddedSettings);
            _embeddedSensor.Initialize();

            _embeddedUdpServer = new DsuUdpServer(
                _embeddedSensor,
                _embeddedSettings.UdpPort,
                (byte)_embeddedSettings.ActiveSlot,
                _embeddedSettings.SecondaryUdpPort);
            _embeddedUdpServer.ToggleStreaming(_embeddedSettings.IsStreamingEnabled);
            _embeddedUdpServer.ForwardGamepadInputs = _embeddedSettings.ForwardGamepadInputs;
            if (_embeddedSettings.IsSecondaryClusterActive)
            {
                _embeddedUdpServer.SetSecondaryClusterActive(true, _embeddedSettings.SecondaryUdpPort);
            }

            _embeddedIpcServer = new IpcHttpServer(_embeddedSensor, _embeddedUdpServer, _embeddedSettings, IpcHttpServer.DefaultIpcPort);

            CancellationToken token = _embeddedCts.Token;
            _ = Task.Run(() => _embeddedUdpServer.RunAsync(token), token);
            _ = Task.Run(() => _embeddedIpcServer.RunAsync(token), token);

            // Start an automatic 1-second calibration if no prior bias offsets were saved
            if (_embeddedSettings.BiasX == 0f && _embeddedSettings.BiasY == 0f && _embeddedSettings.BiasZ == 0f)
            {
                _ = Task.Run(async () =>
                {
                    await _embeddedSensor.CalibrateAsync(token);
                    _embeddedIpcServer.SaveCurrentSettings();
                }, token);
            }

            _usingEmbeddedEngine = true;
        }
    }

    /// <summary>
    /// Reads the latest processed IMU sample directly from the embedded sensor engine at high frequency
    /// without HTTP serialization overhead.
    /// </summary>
    public bool TryGetLiveMotionSample(out MotionData motion)
    {
        EnsureEmbeddedEngineStarted();
        if (_embeddedSensor is not null)
        {
            motion = _embeddedSensor.GetProcessedMotionData();
            return true;
        }

        motion = default;
        return false;
    }

    private DsuStatusDto BuildEmbeddedStatusDto(string? customMessage = null)
    {
        EnsureEmbeddedEngineStarted();

        MotionData motion = _embeddedSensor!.GetProcessedMotionData();
        GyroBias bias = _embeddedSensor.CurrentBias;
        DeviceProfile profile = _embeddedSensor.ActiveProfile;
        IReadOnlyList<IPEndPoint> clients = _embeddedUdpServer!.GetActiveClients();

        return new DsuStatusDto
        {
            Message = customMessage ?? "OK",
            Language = _embeddedSettings?.Language ?? "en",
            ActiveProfileName = _embeddedSettings?.ActiveProfileName ?? "Standard Landscape",
            ServerPort = _embeddedUdpServer.Port,
            ActiveSlot = _embeddedUdpServer.ActiveSlot,
            WasPortFallbackTriggered = _embeddedUdpServer.WasPortFallbackTriggered,
            IsSecondaryClusterActive = _embeddedUdpServer.IsSecondaryClusterActive,
            SecondaryPort = _embeddedUdpServer.SecondaryPort,
            IpcPort = IpcHttpServer.DefaultIpcPort,
            IsStreamingEnabled = _embeddedUdpServer.IsStreamingEnabled,
            ForwardGamepadInputs = _embeddedUdpServer.ForwardGamepadInputs,
            StartWithWindows = _embeddedSettings?.StartWithWindows ?? false,
            CloseToTray = _embeddedSettings?.CloseToTray ?? false,
            GameBarOverlayEnabled = _embeddedSettings?.GameBarOverlayEnabled ?? true,
            HudNotificationsEnabled = _embeddedSettings?.HudNotificationsEnabled ?? true,
            TabletopCalibrationGuard = _embeddedSensor.TabletopCalibrationGuard,
            IsSyntheticWaveEnabled = _embeddedSensor.IsSyntheticWaveEnabled,
            IsEmulatorConnected = clients.Count > 0,
            ConnectedClients = clients.Select(c => c.ToString()).ToArray(),
            TotalPacketsSent = _embeddedUdpServer.TotalPacketsSent,
            InvertX = profile.InvertX,
            InvertY = profile.InvertY,
            InvertZ = profile.InvertZ,
            SwapAxes = profile.SwapAxes,
            UsePitchOffset = _embeddedSensor.UsePitchOffset,
            Sensitivity = Math.Round(_embeddedSensor.Sensitivity, 2),
            Deadzone = Math.Round(_embeddedSensor.Deadzone, 2),
            IsCalibrating = _embeddedSensor.IsCalibrating,
            CalibrationProgress = _embeddedSensor.CalibrationProgressPercent,
            GyroPitch = Math.Round(motion.GyroPitch, 2),
            GyroYaw = Math.Round(motion.GyroYaw, 2),
            GyroRoll = Math.Round(motion.GyroRoll, 2),
            AccX = Math.Round(motion.AccX, 3),
            AccY = Math.Round(motion.AccY, 3),
            AccZ = Math.Round(motion.AccZ, 3),
            BiasX = Math.Round(bias.BiasX, 3),
            BiasY = Math.Round(bias.BiasY, 3),
            BiasZ = Math.Round(bias.BiasZ, 3)
        };
    }

    public async Task<DsuStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            try
            {
                string json = await _httpClient.GetStringAsync("/status", cancellationToken);
                DsuStatusDto? dto = JsonSerializer.Deserialize<DsuStatusDto>(json);
                if (dto is not null)
                    return dto;
            }
            catch
            {
                EnsureEmbeddedEngineStarted();
            }
        }

        return BuildEmbeddedStatusDto();
    }

    public async Task<DsuStatusDto> CalibrateAsync(CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            DsuStatusDto? remote = await PostJsonAsync("/calibrate", "{}", cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        if (!_embeddedSensor!.IsCalibrating)
        {
            _ = Task.Run(async () =>
            {
                await _embeddedSensor.CalibrateAsync(cancellationToken);
                _embeddedIpcServer?.SaveCurrentSettings();
            }, cancellationToken);
        }

        await Task.Delay(30, cancellationToken);
        return BuildEmbeddedStatusDto("Calibration started");
    }

    public async Task<DsuStatusDto> RecenterImmediateAsync(CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            DsuStatusDto? remote = await PostJsonAsync("/recenter", "{}", cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        _embeddedSensor!.RecenterImmediate();
        _embeddedIpcServer?.SaveCurrentSettings();
        return BuildEmbeddedStatusDto("Instant recenter applied");
    }

    public async Task<DsuStatusDto> ToggleStreamingAsync(bool? enabled = null, CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            string payload = enabled.HasValue
                ? JsonSerializer.Serialize(new { enabled = enabled.Value })
                : "{}";
            DsuStatusDto? remote = await PostJsonAsync("/toggle", payload, cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        _embeddedUdpServer!.ToggleStreaming(enabled);
        _embeddedIpcServer?.SaveCurrentSettings();
        return BuildEmbeddedStatusDto();
    }

    public async Task<(bool Success, DsuStatusDto Status)> ChangePortAsync(int newPort, CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            string payload = JsonSerializer.Serialize(new { port = newPort });
            DsuStatusDto? remote = await PostJsonAsync("/port", payload, cancellationToken);
            if (remote is not null)
                return (remote.ServerPort == newPort, remote);

            EnsureEmbeddedEngineStarted();
        }

        bool ok = _embeddedUdpServer!.TryChangePort(newPort, out string err);
        if (ok)
        {
            _embeddedIpcServer?.SaveCurrentSettings();
        }
        return (ok, BuildEmbeddedStatusDto(ok ? $"UDP port {newPort} active" : err));
    }

    public async Task<DsuStatusDto> SetStartupAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            string payload = JsonSerializer.Serialize(new { enabled });
            DsuStatusDto? remote = await PostJsonAsync("/startup", payload, cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        StartupManager.SetStartup(enabled);
        _embeddedIpcServer?.SaveCurrentSettings();
        return BuildEmbeddedStatusDto();
    }

    public async Task<DsuStatusDto> UpdateAxesAsync(
        bool? invertX = null,
        bool? invertY = null,
        bool? invertZ = null,
        bool? swapAxes = null,
        bool? usePitchOffset = null,
        float? sensitivity = null,
        float? deadzone = null,
        CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            string payload = JsonSerializer.Serialize(new
            {
                invertX,
                invertY,
                invertZ,
                swapAxes,
                usePitchOffset,
                sensitivity,
                deadzone
            });
            DsuStatusDto? remote = await PostJsonAsync("/axes", payload, cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        _embeddedSensor!.UpdateAxisSettings(invertX, invertY, invertZ, swapAxes, usePitchOffset, sensitivity, deadzone);
        _embeddedIpcServer?.SaveCurrentSettings();
        return BuildEmbeddedStatusDto();
    }

    public async Task<DsuStatusDto> ResetAxesAsync(CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            string payload = JsonSerializer.Serialize(new { reset = true });
            DsuStatusDto? remote = await PostJsonAsync("/axes", payload, cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        _embeddedSensor!.ResetAxisSettings();
        _embeddedIpcServer?.SaveCurrentSettings();
        return BuildEmbeddedStatusDto();
    }

    public async Task<DsuStatusDto> SetLanguageAsync(string language, CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            string payload = JsonSerializer.Serialize(new { language });
            DsuStatusDto? remote = await PostJsonAsync("/language", payload, cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        _embeddedIpcServer?.SetLanguage(language);
        return BuildEmbeddedStatusDto();
    }

    public async Task<IReadOnlyList<HandheldProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            try
            {
                string json = await _httpClient.GetStringAsync("/profiles", cancellationToken);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("profiles", out JsonElement arrayProp))
                {
                    var parsed = JsonSerializer.Deserialize<List<HandheldProfile>>(arrayProp.GetRawText());
                    if (parsed is not null && parsed.Count > 0)
                        return parsed;
                }
            }
            catch
            {
                // Fall back to local ProfileManager
            }
        }

        return ProfileManager.LoadAllProfiles();
    }

    public async Task<DsuStatusDto> ApplyProfileAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            string payload = JsonSerializer.Serialize(new { name });
            DsuStatusDto? remote = await PostJsonAsync("/profiles/apply", payload, cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        var profiles = ProfileManager.LoadAllProfiles();
        var target = profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (target is not null)
        {
            _embeddedSensor!.UpdateAxisSettings(
                target.InvertX,
                target.InvertY,
                target.InvertZ,
                target.SwapAxes,
                target.UsePitchOffset,
                target.Sensitivity,
                target.Deadzone);
            if (_embeddedSettings is not null)
            {
                _embeddedSettings.ActiveProfileName = target.Name;
            }
            _embeddedIpcServer?.SaveCurrentSettings();
        }
        return BuildEmbeddedStatusDto();
    }

    public async Task<DsuStatusDto> SaveProfileAsync(HandheldProfile profile, CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            string payload = JsonSerializer.Serialize(profile);
            DsuStatusDto? remote = await PostJsonAsync("/profiles/save", payload, cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        ProfileManager.SaveProfile(profile);
        if (_embeddedSettings is not null)
        {
            _embeddedSettings.ActiveProfileName = profile.Name;
        }
        _embeddedIpcServer?.SaveCurrentSettings();
        return BuildEmbeddedStatusDto();
    }

    public async Task<DsuStatusDto> DeleteProfileAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            string payload = JsonSerializer.Serialize(new { name });
            DsuStatusDto? remote = await PostJsonAsync("/profiles/delete", payload, cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        ProfileManager.DeleteProfile(name);
        return BuildEmbeddedStatusDto();
    }

    public async Task<DsuStatusDto> SetActiveSlotAsync(int slot, CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            string payload = JsonSerializer.Serialize(new { slot });
            DsuStatusDto? remote = await PostJsonAsync("/slot", payload, cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        _embeddedUdpServer!.SetActiveSlot(slot);
        _embeddedIpcServer?.SaveCurrentSettings();
        return BuildEmbeddedStatusDto();
    }

    public bool SetSecondaryClusterActive(bool enabled)
    {
        EnsureEmbeddedEngineStarted();
        bool result = _embeddedUdpServer!.SetSecondaryClusterActive(enabled);
        _embeddedIpcServer?.SaveCurrentSettings();
        return result;
    }

    public async Task<(bool Success, DsuStatusDto Status)> ChangeSecondaryPortAsync(int newSecondaryPort, CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            string payload = JsonSerializer.Serialize(new { port = newSecondaryPort });
            DsuStatusDto? remote = await PostJsonAsync("/cluster", payload, cancellationToken);
            if (remote is not null)
                return (remote.SecondaryPort == newSecondaryPort, remote);

            EnsureEmbeddedEngineStarted();
        }

        bool ok = _embeddedUdpServer!.TryChangeSecondaryPort(newSecondaryPort, out string err);
        if (ok)
        {
            _embeddedIpcServer?.SaveCurrentSettings();
        }
        return (ok, BuildEmbeddedStatusDto(ok ? $"Secondary UDP port {newSecondaryPort} saved" : err));
    }

    public async Task<DsuStatusDto> UpdatePreferencesAsync(
        bool? closeToTray = null,
        bool? gameBarOverlayEnabled = null,
        bool? hudNotificationsEnabled = null,
        bool? syntheticWaveEnabled = null,
        bool? rebindSensors = null,
        bool? forwardGamepadInputs = null,
        bool? tabletopCalibrationGuard = null,
        CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            string payload = JsonSerializer.Serialize(new
            {
                closeToTray,
                gameBarOverlayEnabled,
                hudNotificationsEnabled,
                syntheticWaveEnabled,
                rebindSensors,
                forwardGamepadInputs,
                tabletopCalibrationGuard
            });
            DsuStatusDto? remote = await PostJsonAsync("/preferences", payload, cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        _embeddedIpcServer?.SetPreferences(
            closeToTray,
            gameBarOverlayEnabled,
            hudNotificationsEnabled,
            syntheticWaveEnabled,
            rebindSensors,
            forwardGamepadInputs,
            tabletopCalibrationGuard);
        return BuildEmbeddedStatusDto();
    }

    public async Task<DsuStatusDto> ResetAllSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            DsuStatusDto? remote = await PostJsonAsync("/reset", "{}", cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        _embeddedIpcServer?.ResetToFactoryDefaults();
        return BuildEmbeddedStatusDto("All settings reset to defaults");
    }

    private async Task<DsuStatusDto?> PostJsonAsync(string path, string jsonBody, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync(path, content, cancellationToken);
            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<DsuStatusDto>(responseJson);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        try
        {
            _embeddedCts?.Cancel();
            _embeddedIpcServer?.Dispose();
            _embeddedUdpServer?.Dispose();
            _embeddedSensor?.Dispose();
            _embeddedCts?.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }
    }
}
