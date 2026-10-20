using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HandheldDsuServer.Models;
using HandheldDsuServer.Protocol;

namespace HandheldDsuServer.Services;

/// <summary>
/// UDP (varsayılan 0.0.0.0:26760, çalışma zamanında canlı değiştirilebilir) portu üzerinde çalışan Cemuhook DSU sunucusu.
/// Cemu, Yuzu, Eden, Ryujinx, Dolphin, Citra ve RPCS3 gibi emülatörlerle tam uyumludur.
/// </summary>
public sealed class DsuUdpServer : IDisposable
{
    public const int DefaultPort = 26760;
    private static readonly TimeSpan ClientSubscriptionTimeout = TimeSpan.FromSeconds(5);

    private readonly SensorService _sensorService;
    private readonly uint _serverId;
    private readonly object _socketLock = new();

    private volatile int _port;
    private volatile UdpClient? _udpClient;
    private readonly ConcurrentDictionary<IPEndPoint, DateTime> _subscribedClients = new();

    private uint _packetNumber;
    private long _totalPacketsSent;
    private volatile bool _isStreamingEnabled = true;

    public int Port => _port;
    public uint ServerId => _serverId;
    public long TotalPacketsSent => Interlocked.Read(ref _totalPacketsSent);
    public bool IsStreamingEnabled => _isStreamingEnabled;
    public string LastClientPacketDebug { get; private set; } = "NONE";

    public DsuUdpServer(SensorService sensorService, int port = DefaultPort)
    {
        _sensorService = sensorService;
        _port = port is >= 1024 and <= 65535 ? port : DefaultPort;
        _serverId = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
    }

    public bool ToggleStreaming(bool? enabled = null)
    {
        _isStreamingEnabled = enabled ?? !_isStreamingEnabled;
        return _isStreamingEnabled;
    }

    /// <summary>
    /// UDP sunucu portunu uygulamayı yeniden başlatmaya gerek kalmadan canlı olarak değiştirir.
    /// </summary>
    public bool TryChangePort(int newPort, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (newPort is < 1024 or > 65535)
        {
            errorMessage = "Port numarası 1024 ile 65535 arasında olmalıdır.";
            return false;
        }

        if (newPort == _port && _udpClient is not null)
            return true;

        lock (_socketLock)
        {
            UdpClient? newClient = null;
            try
            {
                newClient = CreateConfiguredUdpClient(newPort);
            }
            catch (Exception ex)
            {
                newClient?.Dispose();
                errorMessage = $"Port {newPort} açılamadı (başka bir uygulama kullanıyor olabilir): {ex.Message}";
                return false;
            }

            UdpClient? oldClient = _udpClient;
            _port = newPort;
            _udpClient = newClient;
            _subscribedClients.Clear();

            try
            {
                oldClient?.Close();
                oldClient?.Dispose();
            }
            catch
            {
                // Yoksay
            }

            return true;
        }
    }

    private static UdpClient CreateConfiguredUdpClient(int port)
    {
        var localEndPoint = new IPEndPoint(IPAddress.Any, port);
        var client = new UdpClient(localEndPoint);

        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            client.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }

        return client;
    }

    /// <summary>
    /// Şu anda abone olan aktif emülatör istemcilerinin (IP:Port) listesini döner.
    /// </summary>
    public IReadOnlyList<IPEndPoint> GetActiveClients()
    {
        DateTime now = DateTime.UtcNow;
        var active = new List<IPEndPoint>();

        foreach (var kvp in _subscribedClients)
        {
            if (now - kvp.Value <= ClientSubscriptionTimeout)
            {
                active.Add(kvp.Key);
            }
            else
            {
                _subscribedClients.TryRemove(kvp.Key, out _);
            }
        }

        return active;
    }

    /// <summary>
    /// UDP sunucusunu başlatır; istemci dinleme ve hareket verisi yayın görevlerini yürütür.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_socketLock)
        {
            _udpClient ??= CreateConfiguredUdpClient(_port);
        }

        Task receiveTask = ReceiveLoopAsync(cancellationToken);
        Task broadcastTask = BroadcastMotionLoopAsync(cancellationToken);

        await Task.WhenAll(receiveTask, broadcastTask);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpClient? currentClient = _udpClient;
            if (currentClient is null)
            {
                await Task.Delay(50, cancellationToken);
                continue;
            }

            try
            {
                UdpReceiveResult result = await currentClient.ReceiveAsync(cancellationToken);
                byte[] buffer = result.Buffer;
                IPEndPoint clientEndPoint = result.RemoteEndPoint;

                bool parsed = DsuProtocol.TryParseClientPacket(buffer, out uint clientId, out uint messageType);
                if (!parsed || messageType == DsuProtocol.MsgTypeControllerInfoRequest || LastClientPacketDebug == "NONE")
                {
                    LastClientPacketDebug = $"From={clientEndPoint} Len={buffer.Length} Parsed={parsed} Msg=0x{messageType:X6} Hex={Convert.ToHexString(buffer)}";
                }

                if (!parsed)
                    continue;

                switch (messageType)
                {
                    case DsuProtocol.MsgTypeVersionRequest:
                        await HandleVersionRequestAsync(currentClient, clientEndPoint, cancellationToken);
                        break;

                    case DsuProtocol.MsgTypeControllerInfoRequest:
                        await HandleControllerInfoRequestAsync(currentClient, clientEndPoint, buffer, cancellationToken);
                        break;

                    case DsuProtocol.MsgTypeControllerDataRequest:
                        await HandleControllerDataRequestAsync(currentClient, clientEndPoint, buffer, cancellationToken);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Port değiştirildiğinde eski soket kapatılır; döngü yeni soketle devam eder
            }
            catch (SocketException)
            {
                // Geçici ağ hatalarında veya port değişiminde dinlemeye devam et
            }
            catch
            {
                // Bozuk paketleri yoksay
            }
        }
    }

    private async Task HandleVersionRequestAsync(
        UdpClient udpClient,
        IPEndPoint clientEndPoint,
        CancellationToken cancellationToken)
    {
        byte[] response = DsuProtocol.BuildVersionResponse(_serverId);
        await udpClient.SendAsync(response, clientEndPoint, cancellationToken);
    }

    private async Task HandleControllerInfoRequestAsync(
        UdpClient udpClient,
        IPEndPoint clientEndPoint,
        byte[] requestBuffer,
        CancellationToken cancellationToken)
    {
        if (requestBuffer.Length < 24)
        {
            byte[] fallback = DsuProtocol.BuildControllerInfoResponse(_serverId, 0);
            await udpClient.SendAsync(fallback, clientEndPoint, cancellationToken);
            return;
        }

        int numPorts = BinaryPrimitives.ReadInt32LittleEndian(requestBuffer.AsSpan(20, 4));
        numPorts = Math.Clamp(numPorts, 1, 8);

        for (int i = 0; i < numPorts; i++)
        {
            byte requestedSlot = (24 + i < requestBuffer.Length) ? requestBuffer[24 + i] : (byte)i;
            byte[] response = DsuProtocol.BuildControllerInfoResponse(_serverId, requestedSlot);
            await udpClient.SendAsync(response, clientEndPoint, cancellationToken);
        }

        // Cemu DSUControllerProvider (kMaxClients = 8) wait_update döngüsü 0..7 arasındaki
        // tüm slotların yanıtlanmasını bekler; aksi halde açılır menüyü 3000 ms boyunca
        // "Searching for controllers..." durumunda bloklar.
        if (numPorts == 4)
        {
            for (byte extraSlot = 4; extraSlot < 8; extraSlot++)
            {
                byte[] response = DsuProtocol.BuildControllerInfoResponse(_serverId, extraSlot);
                await udpClient.SendAsync(response, clientEndPoint, cancellationToken);
            }
        }
    }

    private async Task HandleControllerDataRequestAsync(
        UdpClient udpClient,
        IPEndPoint clientEndPoint,
        byte[] requestBuffer,
        CancellationToken cancellationToken)
    {
        if (requestBuffer.Length >= 22)
        {
            byte regFlags = requestBuffer[20];
            byte slot = requestBuffer[21];

            if (regFlags == 1 && slot != 0)
                return;
        }

        _subscribedClients[clientEndPoint] = DateTime.UtcNow;

        if (_isStreamingEnabled)
        {
            byte[] immediatePacket = new byte[100];
            MotionData motion = _sensorService.GetProcessedMotionData();
            uint currentPacketNum = Interlocked.Increment(ref _packetNumber);
            DsuProtocol.BuildMotionDataPacket(immediatePacket, _serverId, currentPacketNum, in motion);
            await udpClient.SendAsync(immediatePacket, clientEndPoint, cancellationToken);
            Interlocked.Increment(ref _totalPacketsSent);
        }
    }

    private async Task BroadcastMotionLoopAsync(CancellationToken cancellationToken)
    {
        uint intervalMs = _sensorService.GyroReportIntervalMs is >= 4 and <= 10
            ? _sensorService.GyroReportIntervalMs
            : 4u;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        byte[] packetBuffer = new byte[100];

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken))
                    break;

                if (!_isStreamingEnabled || _subscribedClients.IsEmpty)
                    continue;

                UdpClient? currentClient = _udpClient;
                if (currentClient is null)
                    continue;

                IReadOnlyList<IPEndPoint> activeClients = GetActiveClients();
                if (activeClients.Count == 0)
                    continue;

                MotionData motion = _sensorService.GetProcessedMotionData();
                uint currentPacketNum = Interlocked.Increment(ref _packetNumber);

                DsuProtocol.BuildMotionDataPacket(packetBuffer, _serverId, currentPacketNum, in motion);

                foreach (IPEndPoint client in activeClients)
                {
                    await currentClient.SendAsync(packetBuffer, client, cancellationToken);
                    Interlocked.Increment(ref _totalPacketsSent);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Gönderim hatalarında döngüyü kırma
            }
        }
    }

    public void Dispose()
    {
        lock (_socketLock)
        {
            _udpClient?.Dispose();
            _udpClient = null;
        }
    }
}
