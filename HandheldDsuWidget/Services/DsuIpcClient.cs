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

    [JsonPropertyName("serverPort")]
    public int ServerPort { get; set; } = 26760;

    [JsonPropertyName("ipcPort")]
    public int IpcPort { get; set; } = 26761;

    [JsonPropertyName("isStreamingEnabled")]
    public bool IsStreamingEnabled { get; set; } = true;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    [JsonPropertyName("isEmulatorConnected")]
    public bool IsEmulatorConnected { get; set; }

    [JsonPropertyName("connectedClients")]
    public string[] ConnectedClients { get; set; } = [];

    [JsonPropertyName("totalPacketsSent")]
    public long TotalPacketsSent { get; set; }

    [JsonPropertyName("activeProfile")]
    public string ActiveProfile { get; set; } = "Asus ROG Ally (Varsayılan)";

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

    [JsonPropertyName("isCalibrating")]
    public bool IsCalibrating { get; set; }

    [JsonPropertyName("calibrationProgress")]
    public int CalibrationProgress { get; set; } = 100;

    [JsonPropertyName("isSimulatingShake")]
    public bool IsSimulatingShake { get; set; }

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
/// DSU arka plan motoruyla (http://127.0.0.1:26761) iletişim kurar.
/// Harici sunucu çalışmıyorsa gömülü DSU motorunu settings.json ayarlarıyla otomatik başlatır.
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

            _embeddedSensor = new SensorService(DeviceProfile.CreateAsusRogAlly(), _embeddedSettings);
            _embeddedSensor.Initialize();

            _embeddedUdpServer = new DsuUdpServer(_embeddedSensor, _embeddedSettings.UdpPort);
            _embeddedUdpServer.ToggleStreaming(_embeddedSettings.IsStreamingEnabled);

            _embeddedIpcServer = new IpcHttpServer(_embeddedSensor, _embeddedUdpServer, _embeddedSettings, IpcHttpServer.DefaultIpcPort);

            CancellationToken token = _embeddedCts.Token;
            _ = Task.Run(() => _embeddedUdpServer.RunAsync(token), token);
            _ = Task.Run(() => _embeddedIpcServer.RunAsync(token), token);

            // Eğer daha önce kaydedilmiş bir kalibrasyon yoksa otomatik 1 sn kalibrasyon başlat
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

    private DsuStatusDto BuildEmbeddedStatusDto(string? customMessage = null)
    {
        EnsureEmbeddedEngineStarted();

        MotionData motion = _embeddedSensor!.GetProcessedMotionData();
        GyroBias bias = _embeddedSensor.CurrentBias;
        DeviceProfile profile = _embeddedSensor.ActiveProfile;
        IReadOnlyList<IPEndPoint> clients = _embeddedUdpServer!.GetActiveClients();

        return new DsuStatusDto
        {
            Message = customMessage ?? _embeddedUdpServer.LastClientPacketDebug,
            ServerPort = _embeddedUdpServer.Port,
            IpcPort = IpcHttpServer.DefaultIpcPort,
            IsStreamingEnabled = _embeddedUdpServer.IsStreamingEnabled,
            StartWithWindows = StartupManager.IsStartupEnabled(),
            IsEmulatorConnected = clients.Count > 0,
            ConnectedClients = clients.Select(c => c.ToString()).ToArray(),
            TotalPacketsSent = _embeddedUdpServer.TotalPacketsSent,
            ActiveProfile = profile.Name,
            InvertX = profile.InvertX,
            InvertY = profile.InvertY,
            InvertZ = profile.InvertZ,
            SwapAxes = profile.SwapAxes,
            UsePitchOffset = _embeddedSensor.UsePitchOffset,
            IsCalibrating = _embeddedSensor.IsCalibrating,
            CalibrationProgress = _embeddedSensor.CalibrationProgressPercent,
            IsSimulatingShake = _embeddedSensor.IsSimulatingShake,
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
        return BuildEmbeddedStatusDto("Kalibrasyon başlatıldı");
    }

    public async Task<DsuStatusDto> SetProfileAsync(string profileKey, CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            string payload = JsonSerializer.Serialize(new { profile = profileKey });
            DsuStatusDto? remote = await PostJsonAsync("/profile", payload, cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        _embeddedSensor!.SetOrCycleProfile(profileKey);
        _embeddedIpcServer?.SaveCurrentSettings();
        return BuildEmbeddedStatusDto();
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

    public async Task<DsuStatusDto> TriggerShakeAsync(CancellationToken cancellationToken = default)
    {
        if (!_usingEmbeddedEngine)
        {
            DsuStatusDto? remote = await PostJsonAsync("/shake", "{}", cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        _embeddedSensor!.TriggerShake(4500);
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
        return (ok, BuildEmbeddedStatusDto(ok ? $"UDP Port {newPort} aktif" : err));
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
                usePitchOffset
            });
            DsuStatusDto? remote = await PostJsonAsync("/axes", payload, cancellationToken);
            if (remote is not null)
                return remote;

            EnsureEmbeddedEngineStarted();
        }

        _embeddedSensor!.UpdateAxisSettings(invertX, invertY, invertZ, swapAxes, usePitchOffset);
        _embeddedIpcServer?.SaveCurrentSettings();
        return BuildEmbeddedStatusDto();
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
            // Yoksay
        }
    }
}
