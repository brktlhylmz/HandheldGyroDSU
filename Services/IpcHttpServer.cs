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
/// Xbox Game Bar Widget ve harici araçların DSU Sunucu motorunu kontrol edebilmesi için
/// http://127.0.0.1:26761 üzerinde çalışan hafif yerel HTTP/JSON IPC sunucusu.
/// </summary>
public sealed class IpcHttpServer : IDisposable
{
    public const int DefaultIpcPort = 26761;

    private readonly SensorService _sensorService;
    private readonly DsuUdpServer _udpServer;
    private readonly AppSettings _settings;
    private readonly int _port;
    private TcpListener? _listener;

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
    }

    public void SaveCurrentSettings()
    {
        GyroBias bias = _sensorService.CurrentBias;
        DeviceProfile profile = _sensorService.ActiveProfile;
        _settings.UdpPort = _udpServer.Port;
        _settings.ActiveProfileKey = SensorService.GetProfileKey(profile);
        _settings.InvertX = profile.InvertX;
        _settings.InvertY = profile.InvertY;
        _settings.InvertZ = profile.InvertZ;
        _settings.SwapAxes = profile.SwapAxes;
        _settings.UsePitchOffset = _sensorService.UsePitchOffset;
        _settings.RestAccY = _sensorService.RestAccY;
        _settings.RestAccZ = _sensorService.RestAccZ;
        _settings.IsStreamingEnabled = _udpServer.IsStreamingEnabled;
        _settings.StartWithWindows = StartupManager.IsStartupEnabled();
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
            // Normal kapanış
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal kapanış
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

                    string json = BuildStatusJson("Kalibrasyon başlatıldı (1 sn)");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/profile")
                {
                    string? requestedProfile = ExtractStringParameter(query, body, "profile", "name");
                    DeviceProfile updated = _sensorService.SetOrCycleProfile(requestedProfile);
                    SaveCurrentSettings();
                    string json = BuildStatusJson($"Aktif profil değiştirildi: {updated.Name}");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/toggle")
                {
                    bool? requestedState = ExtractBooleanParameter(query, body, "enabled");
                    bool isEnabled = _udpServer.ToggleStreaming(requestedState);
                    SaveCurrentSettings();
                    string json = BuildStatusJson(isEnabled ? "DSU veri akışı açıldı" : "DSU veri akışı duraklatıldı");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/shake")
                {
                    _sensorService.TriggerShake(4500);
                    string json = BuildStatusJson("4.5 sn Emülatör Shake sinyali başlatıldı");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/port")
                {
                    int? requestedPort = ExtractIntParameter(query, body, "port");
                    string err = "Geçersiz port numarası";
                    if (requestedPort.HasValue && _udpServer.TryChangePort(requestedPort.Value, out err))
                    {
                        SaveCurrentSettings();
                        string json = BuildStatusJson($"UDP portu {requestedPort.Value} olarak güncellendi");
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
                    string json = BuildStatusJson(current ? "Windows ile otomatik başlatma açıldı" : "Windows ile otomatik başlatma kapatıldı");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else if (method == "POST" && path == "/axes")
                {
                    bool? invX = ExtractBooleanParameter(query, body, "invertX");
                    bool? invY = ExtractBooleanParameter(query, body, "invertY");
                    bool? invZ = ExtractBooleanParameter(query, body, "invertZ");
                    bool? swap = ExtractBooleanParameter(query, body, "swapAxes");
                    bool? pitchOff = ExtractBooleanParameter(query, body, "usePitchOffset");
                    _sensorService.UpdateAxisSettings(invX, invY, invZ, swap, pitchOff);
                    SaveCurrentSettings();
                    string json = BuildStatusJson("Eksen yönleri güncellendi");
                    await WriteHttpResponseAsync(stream, 200, "OK", json, cancellationToken);
                }
                else
                {
                    string notFoundJson = JsonSerializer.Serialize(new
                    {
                        error = "Endpoint bulunamadı. Desteklenenler: GET /status, POST /calibrate, POST /profile, POST /toggle, POST /shake, POST /port, POST /startup"
                    });
                    await WriteHttpResponseAsync(stream, 404, "Not Found", notFoundJson, cancellationToken);
                }
            }
            catch
            {
                // Yoksay
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
            message = message ?? _udpServer.LastClientPacketDebug,
            serverPort = _udpServer.Port,
            ipcPort = _port,
            isStreamingEnabled = _udpServer.IsStreamingEnabled,
            startWithWindows = StartupManager.IsStartupEnabled(),
            isEmulatorConnected = clients.Count > 0,
            connectedClients = clients.Select(c => c.ToString()).ToArray(),
            totalPacketsSent = _udpServer.TotalPacketsSent,
            activeProfile = profile.Name,
            invertX = profile.InvertX,
            invertY = profile.InvertY,
            invertZ = profile.InvertZ,
            swapAxes = profile.SwapAxes,
            usePitchOffset = _sensorService.UsePitchOffset,
            isCalibrating = _sensorService.IsCalibrating,
            calibrationProgress = _sensorService.CalibrationProgressPercent,
            isSimulatingShake = _sensorService.IsSimulatingShake,
            gyroPitch = Math.Round(motion.GyroPitch, 2),
            gyroYaw = Math.Round(motion.GyroYaw, 2),
            gyroRoll = Math.Round(motion.GyroRoll, 2),
            accX = Math.Round(motion.AccX, 3),
            accY = Math.Round(motion.AccY, 3),
            accZ = Math.Round(motion.AccZ, 3),
            biasX = Math.Round(bias.BiasX, 3),
            biasY = Math.Round(bias.BiasY, 3),
            biasZ = Math.Round(bias.BiasZ, 3)
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
                    // Yoksay
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
                    // Yoksay
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
                    // Yoksay
                }
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
            // Yoksay
        }
    }
}
