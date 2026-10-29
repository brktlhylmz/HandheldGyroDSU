using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HandheldDsuServer.Models;

namespace HandheldDsuServer.Services;

/// <summary>
/// Lightweight local HTTP/JSON IPC server running on http://127.0.0.1:26761
/// allowing the desktop/overlay widget and external tools to monitor and control the DSU engine.
/// </summary>
public sealed class IpcHttpServer : IDisposable
{
    public const int DefaultIpcPort = 26761;

    private readonly SensorService _sensorService;
    private readonly DsuUdpServer _udpServer;
    private readonly AppSettings _settings;
    private readonly int _port;
    private TcpListener? _listener;
    private bool _cachedStartWithWindows;

    public int Port => _port;

    public IpcHttpServer(
        SensorService sensorService,
        DsuUdpServer udpServer,
        AppSettings? settings = null,
        int port = DefaultIpcPort)
    {
        _sensorService = sensorService;
        _udpServer = udpServer;
        _settings = settings ?? AppSettings.Load();
        _port = port;
        _cachedStartWithWindows = StartupManager.IsStartupEnabled();
    }

    public string CurrentLanguage => _settings.Language;

    public string SetLanguage(string? language)
    {
        string normalized = (language ?? "en").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "en";

        _settings.Language = normalized;
        SaveCurrentSettings();
        return _settings.Language;
    }

    public void SetPreferences(
        bool? closeToTray = null,
        bool? gameBarOverlayEnabled = null,
        bool? hudNotificationsEnabled = null,
        bool? syntheticWaveEnabled = null,
        bool? rebindSensors = null,
        bool? forwardGamepadInputs = null,
        bool? tabletopCalibrationGuard = null,
        string? updateChannel = null)
    {
        if (closeToTray.HasValue)
            _settings.CloseToTray = closeToTray.Value;
        if (gameBarOverlayEnabled.HasValue)
            _settings.GameBarOverlayEnabled = gameBarOverlayEnabled.Value;
        if (hudNotificationsEnabled.HasValue)
            _settings.HudNotificationsEnabled = hudNotificationsEnabled.Value;
        if (syntheticWaveEnabled.HasValue)
            _sensorService.IsSyntheticWaveEnabled = syntheticWaveEnabled.Value;
        if (rebindSensors == true)
            _sensorService.RequestSensorRebind();
        if (forwardGamepadInputs.HasValue)
        {
            _udpServer.ForwardGamepadInputs = forwardGamepadInputs.Value;
            _settings.ForwardGamepadInputs = forwardGamepadInputs.Value;
        }
        if (tabletopCalibrationGuard.HasValue)
        {
            _sensorService.TabletopCalibrationGuard = tabletopCalibrationGuard.Value;
            _settings.TabletopCalibrationGuard = tabletopCalibrationGuard.Value;
        }
        if (!string.IsNullOrWhiteSpace(updateChannel))
        {
            _settings.UpdateChannel = string.Equals(updateChannel.Trim(), "beta", StringComparison.OrdinalIgnoreCase)
                ? "beta"
                : "stable";
        }
        SaveCurrentSettings();
    }

    public void ResetToFactoryDefaults()
    {
        _udpServer.TryChangePort(26760, out _);
        _udpServer.TryChangeSecondaryPort(26762, out _);
        _udpServer.SetSecondaryClusterActive(false, 26762);
        _udpServer.SetActiveSlot(0);
        _udpServer.ToggleStreaming(true);
        _udpServer.ForwardGamepadInputs = true;
        _sensorService.ResetAxisSettings();
        StartupManager.SetStartup(false);
        _cachedStartWithWindows = false;

        _settings.ActiveProfileName = "Standard Landscape";
        _settings.ForwardGamepadInputs = true;
        _settings.CloseToTray = false;
        _settings.GameBarOverlayEnabled = true;
        _settings.HudNotificationsEnabled = true;
        _settings.TabletopCalibrationGuard = true;
        _settings.UpdateChannel = "stable";
        _sensorService.TabletopCalibrationGuard = true;
        _settings.GyroMouseEnabled = false;
        _settings.GyroMouseAlwaysActive = false;
        _settings.GyroMouseTriggerMask = 0x10000;
        _settings.GyroMouseTriggerDisplay = "LT";
        _settings.GyroMouseSpeed = 1.2f;
        SaveCurrentSettings();
    }

    public void SaveCurrentSettings()
    {
        GyroBias bias = _sensorService.CurrentBias;
        DeviceProfile profile = _sensorService.ActiveProfile;
        _settings.UdpPort = _udpServer.Port;
        _settings.SecondaryUdpPort = _udpServer.SecondaryPort;
        _settings.IsSecondaryClusterActive = _udpServer.IsSecondaryClusterActive;
        _settings.ActiveSlot = _udpServer.ActiveSlot;
        _settings.InvertX = profile.InvertX;
        _settings.InvertY = profile.InvertY;
        _settings.InvertZ = profile.InvertZ;
        _settings.SwapAxes = profile.SwapAxes;
        _settings.UsePitchOffset = _sensorService.UsePitchOffset;
        _settings.TabletopCalibrationGuard = _sensorService.TabletopCalibrationGuard;
        _settings.Sensitivity = _sensorService.Sensitivity;
        _settings.Deadzone = _sensorService.Deadzone;
        _settings.RestAccX = _sensorService.RestAccX;
        _settings.RestAccY = _sensorService.RestAccY;
        _settings.RestAccZ = _sensorService.RestAccZ;
        _settings.IsStreamingEnabled = _udpServer.IsStreamingEnabled;
        _settings.ForwardGamepadInputs = _udpServer.ForwardGamepadInputs;
        _cachedStartWithWindows = StartupManager.IsStartupEnabled();
        _settings.StartWithWindows = _cachedStartWithWindows;
        _settings.BiasX = bias.BiasX;
        _settings.BiasY = bias.BiasY;
        _settings.BiasZ = bias.BiasZ;
        _settings.Save();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using NetworkStream stream = client.GetStream();

                byte[] buffer = new byte[4096];
                int bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead <= 0)
                    return;

                string rawRequest = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                int headerEndIndex = rawRequest.IndexOf("\r\n\r\n", StringComparison.Ordinal);

                string headerPart = headerEndIndex >= 0 ? rawRequest[..headerEndIndex] : rawRequest;
                string body = headerEndIndex >= 0 ? rawRequest[(headerEndIndex + 4)..] : string.Empty;

                int contentLength = ExtractContentLength(headerPart);
                if (contentLength > 0 && Encoding.UTF8.GetByteCount(body) < contentLength && stream.DataAvailable)
                {
                    int extraRead = await stream.ReadAsync(buffer, cancellationToken);
                    if (extraRead > 0)
                    {
                        body += Encoding.UTF8.GetString(buffer, 0, extraRead);
                    }
                }

                int firstLineEnd = headerPart.IndexOf("\r\n", StringComparison.Ordinal);
                string requestLine = firstLineEnd >= 0 ? headerPart[..firstLineEnd] : headerPart;

                string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return;

                string method = parts[0].ToUpperInvariant();
                string rawTarget = parts[1];

                if (method == "OPTIONS")
                {
                    await WriteHttpResponseAsync(stream, 204, "No Content", string.Empty, cancellationToken);
                    return;
                }

                string path = rawTarget;
                string query = string.Empty;
                int qIndex = rawTarget.IndexOf('?');
                if (qIndex >= 0)
                {
                    path = rawTarget[..qIndex];
                    query = rawTarget[(qIndex + 1)..];
                }

                path = path.TrimEnd('/').ToLowerInvariant();
                if (string.IsNullOrEmpty(path))
                    path = "/status";

                if (method == "GET" && path == "/status")
                {
                    string json = BuildStatusJson();
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/calibrate")
                {
                    if (!_sensorService.IsCalibrating)
                    {
                        _ = Task.Run(async () =>
                        {
                            await _sensorService.CalibrateAsync(cancellationToken);
                            SaveCurrentSettings();
                        }, cancellationToken);
                    }

                    string json = BuildStatusJson("Calibration started (1s)");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/recenter")
                {
                    _sensorService.RecenterImmediate();
                    SaveCurrentSettings();
                    string json = BuildStatusJson("Instant recenter applied");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/toggle")
                {
                    bool? requestedState = ExtractBooleanParameter(query, body, "enabled");
                    bool isEnabled = _udpServer.ToggleStreaming(requestedState);
                    SaveCurrentSettings();
                    string json = BuildStatusJson(isEnabled ? "DSU stream enabled" : "DSU stream paused");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/port")
                {
                    int? requestedPort = ExtractIntParameter(query, body, "port");
                    string err = "Invalid port number";
                    if (requestedPort.HasValue && _udpServer.TryChangePort(requestedPort.Value, out err))
                    {
                        SaveCurrentSettings();
                        string json = BuildStatusJson($"UDP port updated to {requestedPort.Value}");
                        await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                    }
                    else
                    {
                        string json = BuildStatusJson(err);
                        await WriteHttpResponseAsync(stream, 400, "Bad Request", json, cancellationToken);
                    }
                }
                else if (method == "POST" && path == "/startup")
                {
                    bool? enableStartup = ExtractBooleanParameter(query, body, "enabled");
                    bool target = enableStartup ?? !StartupManager.IsStartupEnabled();
                    bool current = StartupManager.SetStartup(target);
                    SaveCurrentSettings();
                    string json = BuildStatusJson(current ? "Start with Windows enabled" : "Start with Windows disabled");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/axes")
                {
                    bool? reset = ExtractBooleanParameter(query, body, "reset");
                    if (reset == true)
                    {
                        _sensorService.ResetAxisSettings();
                    }
                    else
                    {
                        bool? invX = ExtractBooleanParameter(query, body, "invertX");
                        bool? invY = ExtractBooleanParameter(query, body, "invertY");
                        bool? invZ = ExtractBooleanParameter(query, body, "invertZ");
                        bool? swap = ExtractBooleanParameter(query, body, "swapAxes");
                        bool? pitchOff = ExtractBooleanParameter(query, body, "usePitchOffset");
                        float? sens = ExtractFloatParameter(body, "sensitivity");
                        float? dz = ExtractFloatParameter(body, "deadzone");
                        _sensorService.UpdateAxisSettings(invX, invY, invZ, swap, pitchOff, sens, dz);
                    }
                    SaveCurrentSettings();
                    string json = BuildStatusJson("Axis orientation settings updated");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "GET" && path == "/profiles")
                {
                    var profiles = ProfileManager.LoadAllProfiles();
                    string json = JsonSerializer.Serialize(new
                    {
                        activeProfileName = _settings.ActiveProfileName,
                        profiles
                    });
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/profiles/apply")
                {
                    string? name = ExtractStringParameter(query, body, "name", "profile");
                    var profiles = ProfileManager.LoadAllProfiles();
                    var target = profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (target is not null)
                    {
                        _sensorService.UpdateAxisSettings(
                            target.InvertX,
                            target.InvertY,
                            target.InvertZ,
                            target.SwapAxes,
                            target.UsePitchOffset,
                            target.Sensitivity,
                            target.Deadzone);
                        _settings.ActiveProfileName = target.Name;
                        _settings.GyroMouseEnabled = target.GyroMouseEnabled;
                        _settings.GyroMouseAlwaysActive = target.GyroMouseAlwaysActive;
                        _settings.GyroMouseTriggerMask = target.GyroMouseTriggerMask == 0 ? 0x10000u : target.GyroMouseTriggerMask;
                        _settings.GyroMouseTriggerDisplay = string.IsNullOrWhiteSpace(target.GyroMouseTriggerDisplay) ? "LT" : target.GyroMouseTriggerDisplay;
                        _settings.GyroMouseSpeed = target.GyroMouseSpeed <= 0.05f ? 1.2f : target.GyroMouseSpeed;
                        SaveCurrentSettings();
                        string json = BuildStatusJson($"Profile applied: {target.Name}");
                        await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                    }
                    else
                    {
                        string json = BuildStatusJson("Profile not found");
                        await WriteHttpResponseAsync(stream, 404, "Not Found", json, cancellationToken);
                    }
                }
                else if (method == "POST" && path == "/profiles/save")
                {
                    HandheldProfile? profile = null;
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        profile = ProfileManager.DeserializeProfile(body);
                    }
                    if (profile is null)
                    {
                        string? name = ExtractStringParameter(query, body, "name", "profileName") ?? "Custom Profile";
                        profile = new HandheldProfile
                        {
                            Name = name,
                            InvertX = _sensorService.ActiveProfile.InvertX,
                            InvertY = _sensorService.ActiveProfile.InvertY,
                            InvertZ = _sensorService.ActiveProfile.InvertZ,
                            SwapAxes = _sensorService.ActiveProfile.SwapAxes,
                            UsePitchOffset = _sensorService.UsePitchOffset,
                            Sensitivity = _sensorService.Sensitivity,
                            Deadzone = _sensorService.Deadzone
                        };
                    }

                    ProfileManager.SaveProfile(profile);
                    _settings.ActiveProfileName = profile.Name;
                    _settings.GyroMouseEnabled = profile.GyroMouseEnabled;
                    _settings.GyroMouseAlwaysActive = profile.GyroMouseAlwaysActive;
                    _settings.GyroMouseTriggerMask = profile.GyroMouseTriggerMask == 0 ? 0x10000u : profile.GyroMouseTriggerMask;
                    _settings.GyroMouseTriggerDisplay = string.IsNullOrWhiteSpace(profile.GyroMouseTriggerDisplay) ? "LT" : profile.GyroMouseTriggerDisplay;
                    _settings.GyroMouseSpeed = profile.GyroMouseSpeed <= 0.05f ? 1.2f : profile.GyroMouseSpeed;
                    SaveCurrentSettings();
                    string json = BuildStatusJson($"Profile saved: {profile.Name}");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/profiles/delete")
                {
                    string? name = ExtractStringParameter(query, body, "name", "profile");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        ProfileManager.DeleteProfile(name);
                    }
                    string json = BuildStatusJson("Profile deleted");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/slot")
                {
                    int? requestedSlot = ExtractIntParameter(query, body, "slot");
                    if (requestedSlot.HasValue)
                    {
                        _udpServer.SetActiveSlot(requestedSlot.Value);
                        SaveCurrentSettings();
                    }
                    string json = BuildStatusJson($"Active DSU slot set to {_udpServer.ActiveSlot}");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/cluster")
                {
                    int? requestedPort = ExtractIntParameter(query, body, "port");
                    bool? requestedEnabled = ExtractBooleanParameter(query, body, "enabled");

                    if (requestedPort.HasValue)
                    {
                        if (!_udpServer.TryChangeSecondaryPort(requestedPort.Value, out string err))
                        {
                            string errJson = BuildStatusJson(err);
                            await WriteHttpResponseAsync(stream, 400, "Bad Request", errJson, cancellationToken);
                            return;
                        }
                    }

                    if (requestedEnabled.HasValue)
                    {
                        _udpServer.SetSecondaryClusterActive(requestedEnabled.Value, requestedPort);
                    }

                    SaveCurrentSettings();
                    string json = BuildStatusJson("Secondary cluster updated");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/preferences")
                {
                    bool? closeTray = ExtractBooleanParameter(query, body, "closeToTray");
                    bool? gameBar = ExtractBooleanParameter(query, body, "gameBarOverlayEnabled");
                    bool? hudNotif = ExtractBooleanParameter(query, body, "hudNotificationsEnabled");
                    bool? synthWave = ExtractBooleanParameter(query, body, "syntheticWaveEnabled");
                    bool? rebind = ExtractBooleanParameter(query, body, "rebindSensors");
                    bool? forwardPad = ExtractBooleanParameter(query, body, "forwardGamepadInputs");
                    bool? calibGuard = ExtractBooleanParameter(query, body, "tabletopCalibrationGuard");
                    string? updChan = ExtractStringParameter(query, body, "updateChannel", "channel");
                    SetPreferences(closeTray, gameBar, hudNotif, synthWave, rebind, forwardPad, calibGuard, updChan);
                    string json = BuildStatusJson("Preferences updated");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/language")
                {
                    string? lang = ExtractStringParameter(query, body, "language", "lang");
                    string updatedLang = SetLanguage(lang);
                    string json = BuildStatusJson($"Language set to {updatedLang}");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/reset")
                {
                    ResetToFactoryDefaults();
                    string json = BuildStatusJson("All settings reset to defaults");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else
                {
                    string notFoundJson = JsonSerializer.Serialize(new
                    {
                        error = "Endpoint not found. Supported: GET /status, POST /calibrate, POST /toggle, POST /port, POST /startup, POST /axes, POST /language"
                    });
                    await WriteHttpResponseAsync(stream, 404, "Not Found", notFoundJson, cancellationToken);
                }
            }
            catch
            {
                // Ignore client connection errors
            }
        }
    }

    private static int ExtractContentLength(string headers)
    {
        foreach (string line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line["Content-Length:".Length..].Trim(), out int len))
                    return len;
            }
        }
        return 0;
    }

    public string BuildStatusJson(string? message = null)
    {
        MotionData motion = _sensorService.GetProcessedMotionData();
        GyroBias bias = _sensorService.CurrentBias;
        DeviceProfile profile = _sensorService.ActiveProfile;
        IReadOnlyList<IPEndPoint> clients = _udpServer.GetActiveClients();

        var payload = new
        {
            message = message ?? "OK",
            language = _settings.Language,
            updateChannel = _settings.UpdateChannel,
            activeProfileName = _settings.ActiveProfileName,
            serverPort = _udpServer.Port,
            activeSlot = (int)_udpServer.ActiveSlot,
            wasPortFallbackTriggered = _udpServer.WasPortFallbackTriggered,
            isSecondaryClusterActive = _udpServer.IsSecondaryClusterActive,
            secondaryPort = _udpServer.SecondaryPort,
            ipcPort = _port,
            isStreamingEnabled = _udpServer.IsStreamingEnabled,
            forwardGamepadInputs = _udpServer.ForwardGamepadInputs,
            startWithWindows = _cachedStartWithWindows,
            closeToTray = _settings.CloseToTray,
            gameBarOverlayEnabled = _settings.GameBarOverlayEnabled,
            hudNotificationsEnabled = _settings.HudNotificationsEnabled,
            tabletopCalibrationGuard = _sensorService.TabletopCalibrationGuard,
            isSyntheticWaveEnabled = _sensorService.IsSyntheticWaveEnabled,
            isEmulatorConnected = clients.Count > 0,
            connectedClients = clients.Select(c => c.ToString()).ToArray(),
            totalPacketsSent = _udpServer.TotalPacketsSent,
            invertX = profile.InvertX,
            invertY = profile.InvertY,
            invertZ = profile.InvertZ,
            swapAxes = profile.SwapAxes,
            usePitchOffset = _sensorService.UsePitchOffset,
            sensitivity = Math.Round(_sensorService.Sensitivity, 2),
            deadzone = Math.Round(_sensorService.Deadzone, 2),
            isCalibrating = _sensorService.IsCalibrating,
            calibrationProgress = _sensorService.CalibrationProgressPercent,
            gyroPitch = Math.Round(motion.GyroPitch, 2),
            gyroYaw = Math.Round(motion.GyroYaw, 2),
            gyroRoll = Math.Round(motion.GyroRoll, 2),
            accX = Math.Round(motion.AccX, 3),
            accY = Math.Round(motion.AccY, 3),
            accZ = Math.Round(motion.AccZ, 3),
            biasX = Math.Round(bias.BiasX, 3),
            biasY = Math.Round(bias.BiasY, 3),
            biasZ = Math.Round(bias.BiasZ, 3),
            gyroMouseEnabled = _settings.GyroMouseEnabled,
            gyroMouseAlwaysActive = _settings.GyroMouseAlwaysActive,
            gyroMouseTriggerMask = _settings.GyroMouseTriggerMask,
            gyroMouseTriggerDisplay = _settings.GyroMouseTriggerDisplay,
            gyroMouseSpeed = Math.Round(_settings.GyroMouseSpeed, 2)
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string? ExtractStringParameter(string query, string body, string key1, string key2)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = pair.Split('=', 2);
                if (kv.Length == 2 && (kv[0].Equals(key1, StringComparison.OrdinalIgnoreCase) ||
                                       kv[0].Equals(key2, StringComparison.OrdinalIgnoreCase)))
                {
                    return Uri.UnescapeDataString(kv[1]);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            string trimmed = body.Trim();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(trimmed);
                    if (doc.RootElement.TryGetProperty(key1, out JsonElement prop))
                        return prop.GetString();
                    if (doc.RootElement.TryGetProperty(key2, out JsonElement prop2))
                        return prop2.GetString();
                }
                catch
                {
                    // Ignore malformed JSON
                }
            }
            return trimmed.Trim('"');
        }

        return null;
    }

    private static int? ExtractIntParameter(string query, string body, string key)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals(key, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(kv[1], out int parsed))
                {
                    return parsed;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            string trimmed = body.Trim();
            if (int.TryParse(trimmed, out int directInt))
                return directInt;

            if (trimmed.StartsWith('{'))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(trimmed);
                    if (doc.RootElement.TryGetProperty(key, out JsonElement prop))
                    {
                        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int num))
                            return num;
                        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out int strNum))
                            return strNum;
                    }
                }
                catch
                {
                    // Ignore malformed JSON
                }
            }
        }

        return null;
    }

    private static bool? ExtractBooleanParameter(string query, string body, string key)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals(key, StringComparison.OrdinalIgnoreCase) &&
                    bool.TryParse(kv[1], out bool parsed))
                {
                    return parsed;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            string trimmed = body.Trim();
            if (bool.TryParse(trimmed, out bool directBool))
                return directBool;

            if (trimmed.StartsWith('{'))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(trimmed);
                    if (doc.RootElement.TryGetProperty(key, out JsonElement prop) &&
                        (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False))
                    {
                        return prop.GetBoolean();
                    }
                }
                catch
                {
                    // Ignore malformed JSON
                }
            }
        }

        return null;
    }

    private static float? ExtractFloatParameter(string body, string key)
    {
        if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith('{'))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body.Trim());
                if (doc.RootElement.TryGetProperty(key, out JsonElement prop) &&
                    prop.ValueKind == JsonValueKind.Number &&
                    prop.TryGetSingle(out float val))
                {
                    return val;
                }
            }
            catch
            {
                // Ignore malformed JSON
            }
        }
        return null;
    }

    private static async Task WriteHttpResponseAsync(
        NetworkStream stream,
        int statusCode,
        string statusText,
        string jsonBody,
        CancellationToken cancellationToken)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
        string header =
            $"HTTP/1.1 {statusCode} {statusText}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Connection: close\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, cancellationToken);
        }
        await stream.FlushAsync(cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Ignore listener stop errors
        }
    }
}
