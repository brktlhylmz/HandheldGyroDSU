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
/// Cemuhook DSU UDP Server (default 0.0.0.0:26760, hot-swappable at runtime).
/// Compatible with Cemu, Yuzu, Eden, Ryujinx, Dolphin, Citra, and RPCS3.
/// </summary>
public sealed class DsuUdpServer : IDisposable
{
    public const int DefaultPort = 26760;
    private static readonly TimeSpan ClientSubscriptionTimeout = TimeSpan.FromSeconds(5);

    private readonly SensorService _sensorService;
    private readonly uint _serverId;
    private readonly object _socketLock = new();

    private volatile int _port;
    private volatile byte _activeSlot;
    private volatile bool _wasPortFallbackTriggered;
    private volatile UdpClient? _udpClient;
    private volatile UdpClient? _secondaryClusterClient;
    private volatile int _secondaryPort;
    private volatile bool _isSecondaryClusterActive;
    private readonly ConcurrentDictionary<IPEndPoint, DateTime> _subscribedClients = new();
    private readonly ConcurrentDictionary<IPEndPoint, DateTime> _secondarySubscribedClients = new();
    private readonly ConcurrentDictionary<IPEndPoint, DateTime> _infoClients = new();
    private readonly ConcurrentDictionary<IPEndPoint, byte> _versionRequestedEndpoints = new();

    private uint _packetNumber;
    private long _totalPacketsSent;
    private volatile bool _isStreamingEnabled = true;
    private volatile bool _forwardGamepadInputs = true;

    public int Port => _port;
    public byte ActiveSlot => _activeSlot;
    public bool WasPortFallbackTriggered => _wasPortFallbackTriggered;
    public bool IsSecondaryClusterActive => _isSecondaryClusterActive;
    public int SecondaryPort => _secondaryPort;
    public uint ServerId => _serverId;
    public long TotalPacketsSent => Interlocked.Read(ref _totalPacketsSent);
    public bool IsStreamingEnabled => _isStreamingEnabled;
    public bool ForwardGamepadInputs
    {
        get => _forwardGamepadInputs;
        set => _forwardGamepadInputs = value;
    }

    public DsuUdpServer(SensorService sensorService, int port = DefaultPort, byte activeSlot = 0, int secondaryPort = 26762)
    {
        _sensorService = sensorService;
        _port = port is >= 1024 and <= 65535 ? port : DefaultPort;
        _activeSlot = (byte)Math.Clamp((int)activeSlot, 0, 3);
        _secondaryPort = (secondaryPort is >= 1024 and <= 65535 && secondaryPort != _port && secondaryPort != 26761)
            ? secondaryPort
            : 26762;
        _serverId = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
    }

    public byte SetActiveSlot(int slot)
    {
        _activeSlot = (byte)Math.Clamp(slot, 0, 3);
        return _activeSlot;
    }

    /// <summary>
    /// Enables or disables the secondary DSU UDP cluster port (customizable port, default 26762)
    /// when 5 to 8 controllers are connected simultaneously.
    /// </summary>
    public bool SetSecondaryClusterActive(bool enabled, int? customPort = null)
    {
        lock (_socketLock)
        {
            if (customPort.HasValue && customPort.Value is >= 1024 and <= 65535 && customPort.Value != _port && customPort.Value != 26761)
            {
                _secondaryPort = customPort.Value;
            }

            if (enabled == _isSecondaryClusterActive && (!enabled || _secondaryClusterClient is not null))
                return _isSecondaryClusterActive;

            if (!enabled)
            {
                _isSecondaryClusterActive = false;
                _secondarySubscribedClients.Clear();
                try
                {
                    _secondaryClusterClient?.Close();
                    _secondaryClusterClient?.Dispose();
                }
                catch
                {
                    // Ignore disposal errors
                }
                _secondaryClusterClient = null;
                return false;
            }

            // Try binding the user's configured secondary port first
            try
            {
                _secondaryClusterClient = CreateConfiguredUdpClient(_secondaryPort);
                _isSecondaryClusterActive = true;
                return true;
            }
            catch
            {
                // Fallback to next available port if the configured secondary port is occupied
                for (int candidate = 26762; candidate <= 26785; candidate++)
                {
                    if (candidate == _port || candidate == 26761)
                        continue;

                    try
                    {
                        _secondaryClusterClient = CreateConfiguredUdpClient(candidate);
                        _secondaryPort = candidate;
                        _isSecondaryClusterActive = true;
                        return true;
                    }
                    catch
                    {
                        // Try next candidate port
                    }
                }
            }

            _isSecondaryClusterActive = false;
            return false;
        }
    }

    /// <summary>
    /// Hot-swaps the secondary cluster UDP port to any user-specified port (1024..65535).
    /// </summary>
    public bool TryChangeSecondaryPort(int newSecondaryPort, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (newSecondaryPort is < 1024 or > 65535 || newSecondaryPort == _port || newSecondaryPort == 26761)
        {
            errorMessage = "Secondary port must be between 1024 and 65535 and cannot equal the primary or IPC port.";
            return false;
        }

        lock (_socketLock)
        {
            if (!_isSecondaryClusterActive)
            {
                _secondaryPort = newSecondaryPort;
                return true;
            }

            if (newSecondaryPort == _secondaryPort && _secondaryClusterClient is not null)
                return true;

            UdpClient? newClient = null;
            try
            {
                newClient = CreateConfiguredUdpClient(newSecondaryPort);
            }
            catch (Exception ex)
            {
                newClient?.Dispose();
                errorMessage = $"Failed to bind secondary UDP port {newSecondaryPort}: {ex.Message}";
                return false;
            }

            UdpClient? oldClient = _secondaryClusterClient;
            _secondaryPort = newSecondaryPort;
            _secondaryClusterClient = newClient;
            _secondarySubscribedClients.Clear();

            try
            {
                oldClient?.Close();
                oldClient?.Dispose();
            }
            catch
            {
                // Ignore old socket disposal errors
            }

            return true;
        }
    }

    public bool ToggleStreaming(bool? enabled = null)
    {
        _isStreamingEnabled = enabled ?? !_isStreamingEnabled;
        return _isStreamingEnabled;
    }

    /// <summary>
    /// Hot-swaps the active UDP listening port without restarting the application.
    /// </summary>
    public bool TryChangePort(int newPort, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (newPort is < 1024 or > 65535)
        {
            errorMessage = "Port number must be between 1024 and 65535.";
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
                errorMessage = $"Failed to bind UDP port {newPort}: {ex.Message}";
                return false;
            }

            UdpClient? oldClient = _udpClient;
            _port = newPort;
            _udpClient = newClient;
            _subscribedClients.Clear();
            _infoClients.Clear();
            _versionRequestedEndpoints.Clear();

            try
            {
                oldClient?.Close();
                oldClient?.Dispose();
            }
            catch
            {
                // Ignore old socket disposal errors
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
    /// Returns the list of currently subscribed or actively polling emulator client endpoints (IP:Port).
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

        foreach (var kvp in _infoClients)
        {
            if (now - kvp.Value <= ClientSubscriptionTimeout)
            {
                if (!active.Contains(kvp.Key))
                {
                    active.Add(kvp.Key);
                }
            }
            else
            {
                _infoClients.TryRemove(kvp.Key, out _);
            }
        }

        return active;
    }

    /// <summary>
    /// Starts the UDP server receive loop and high-frequency motion broadcast loop.
    /// Automatically falls back to the next free port if the initial port is occupied.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_socketLock)
        {
            if (_udpClient is null)
            {
                try
                {
                    _udpClient = CreateConfiguredUdpClient(_port);
                    _wasPortFallbackTriggered = false;
                }
                catch
                {
                    // Auto-fallback if default/configured port is in use by another application (e.g., DS4Windows)
                    for (int candidate = 26762; candidate <= 26780; candidate++)
                    {
                        try
                        {
                            _udpClient = CreateConfiguredUdpClient(candidate);
                            _port = candidate;
                            _secondaryPort = _port + 2;
                            _wasPortFallbackTriggered = true;
                            break;
                        }
                        catch
                        {
                            // Try next candidate
                        }
                    }
                }
            }
        }

        Task receiveTask = ReceiveLoopAsync(cancellationToken);
        Task secondaryReceiveTask = SecondaryClusterReceiveLoopAsync(cancellationToken);
        Task broadcastTask = BroadcastMotionLoopAsync(cancellationToken);

        await Task.WhenAll(receiveTask, secondaryReceiveTask, broadcastTask);
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

                if (!DsuProtocol.TryParseClientPacket(buffer, out _, out uint messageType))
                    continue;

                switch (messageType)
                {
                    case DsuProtocol.MsgTypeVersionRequest:
                        await HandleVersionRequestAsync(currentClient, clientEndPoint, cancellationToken);
                        break;

                    case DsuProtocol.MsgTypeControllerInfoRequest:
                        await HandleControllerInfoRequestAsync(currentClient, clientEndPoint, buffer, _activeSlot, 0, cancellationToken);
                        break;

                    case DsuProtocol.MsgTypeControllerDataRequest:
                        await HandleControllerDataRequestAsync(currentClient, clientEndPoint, buffer, _subscribedClients, _activeSlot, 0, cancellationToken);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Expected when the socket is replaced during a live port change
            }
            catch (SocketException)
            {
                // Continue listening on transient socket errors
            }
            catch
            {
                // Ignore malformed packets
            }
        }
    }

    private async Task SecondaryClusterReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpClient? secondaryClient = _secondaryClusterClient;
            if (!_isSecondaryClusterActive || secondaryClient is null)
            {
                await Task.Delay(150, cancellationToken);
                continue;
            }

            try
            {
                UdpReceiveResult result = await secondaryClient.ReceiveAsync(cancellationToken);
                byte[] buffer = result.Buffer;
                IPEndPoint clientEndPoint = result.RemoteEndPoint;

                if (!DsuProtocol.TryParseClientPacket(buffer, out _, out uint messageType))
                    continue;

                switch (messageType)
                {
                    case DsuProtocol.MsgTypeVersionRequest:
                        await HandleVersionRequestAsync(secondaryClient, clientEndPoint, cancellationToken);
                        break;

                    case DsuProtocol.MsgTypeControllerInfoRequest:
                        await HandleControllerInfoRequestAsync(secondaryClient, clientEndPoint, buffer, activeSlot: 0, macOffset: 4, cancellationToken);
                        break;

                    case DsuProtocol.MsgTypeControllerDataRequest:
                        await HandleControllerDataRequestAsync(secondaryClient, clientEndPoint, buffer, _secondarySubscribedClients, activeSlot: 0, macOffset: 4, cancellationToken);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Ignore transient errors on secondary cluster port
            }
        }
    }

    private async Task HandleVersionRequestAsync(
        UdpClient udpClient,
        IPEndPoint clientEndPoint,
        CancellationToken cancellationToken)
    {
        _versionRequestedEndpoints[clientEndPoint] = 1;
        _infoClients[clientEndPoint] = DateTime.UtcNow;
        byte[] response = DsuProtocol.BuildVersionResponse(_serverId);
        await udpClient.SendAsync(response, clientEndPoint, cancellationToken);
        Interlocked.Increment(ref _totalPacketsSent);
    }

    private async Task HandleControllerInfoRequestAsync(
        UdpClient udpClient,
        IPEndPoint clientEndPoint,
        byte[] requestBuffer,
        byte activeSlot,
        byte macOffset,
        CancellationToken cancellationToken)
    {
        _infoClients[clientEndPoint] = DateTime.UtcNow;

        if (requestBuffer.Length < 24)
        {
            byte[] fallback = DsuProtocol.BuildControllerInfoResponse(_serverId, activeSlot, isSlotConnected: true, macSuffixOffset: macOffset);
            await udpClient.SendAsync(fallback, clientEndPoint, cancellationToken);
            Interlocked.Increment(ref _totalPacketsSent);
            return;
        }

        int numPorts = BinaryPrimitives.ReadInt32LittleEndian(requestBuffer.AsSpan(20, 4));
        numPorts = Math.Clamp(numPorts, 1, 4);

        for (int i = 0; i < numPorts; i++)
        {
            byte requestedSlot = (24 + i < requestBuffer.Length) ? requestBuffer[24 + i] : (byte)i;
            if (requestedSlot >= 4)
                continue;

            bool isConnected = requestedSlot == activeSlot;
            byte[] response = DsuProtocol.BuildControllerInfoResponse(_serverId, requestedSlot, isConnected, macOffset);
            await udpClient.SendAsync(response, clientEndPoint, cancellationToken);
            Interlocked.Increment(ref _totalPacketsSent);
        }

        // Cemu DSUControllerProvider (kMaxClients = 8) always sends 0x100000 (VersionRequest) on the same socket
        // before 0x100001 and waits for slots 0..7 in wait_update().
        // IMPORTANT: Never send slots 4..7 to Dolphin, because Dolphin's DualShockUDPClient stores PortInfo in
        // std::array<PortInfo, 4> m_port_info without bounds-checking pad_id, so pad_id >= 4 overwrites server.m_socket!
        if (numPorts == 4 && _versionRequestedEndpoints.ContainsKey(clientEndPoint))
        {
            for (byte extraSlot = 4; extraSlot < 8; extraSlot++)
            {
                byte[] response = DsuProtocol.BuildControllerInfoResponse(_serverId, extraSlot, isSlotConnected: false, macOffset);
                await udpClient.SendAsync(response, clientEndPoint, cancellationToken);
            }
        }
    }

    private async Task HandleControllerDataRequestAsync(
        UdpClient udpClient,
        IPEndPoint clientEndPoint,
        byte[] requestBuffer,
        ConcurrentDictionary<IPEndPoint, DateTime> clientRegistry,
        byte activeSlot,
        byte macOffset,
        CancellationToken cancellationToken)
    {
        if (requestBuffer.Length >= 22)
        {
            byte regFlags = requestBuffer[20];
            byte slot = requestBuffer[21];

            if (regFlags == 1 && slot != activeSlot)
                return;
        }

        DateTime now = DateTime.UtcNow;
        bool isNewOrStaleSubscription = !clientRegistry.TryGetValue(clientEndPoint, out DateTime lastSeen) ||
                                        (now - lastSeen).TotalMilliseconds > 250;
        clientRegistry[clientEndPoint] = now;

        // Send an immediate packet only when a client first subscribes (or re-subscribes after >250ms).
        // Cemu's DSUControllerProvider sends a new 0x100002 request immediately upon receiving every 0x100002 response;
        // echoing synchronously on every request would cause an unthrottled ping-pong flood.
        if (_isStreamingEnabled && isNewOrStaleSubscription)
        {
            byte[] immediatePacket = new byte[100];
            MotionData motion = _sensorService.GetProcessedMotionData();
            uint currentPacketNum = Interlocked.Increment(ref _packetNumber);
            DsuProtocol.BuildMotionDataPacket(immediatePacket, _serverId, currentPacketNum, in motion, activeSlot, macOffset, _forwardGamepadInputs);
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

                if (!_isStreamingEnabled || (_subscribedClients.IsEmpty && _secondarySubscribedClients.IsEmpty))
                    continue;

                MotionData motion = _sensorService.GetProcessedMotionData();
                uint currentPacketNum = Interlocked.Increment(ref _packetNumber);

                UdpClient? currentClient = _udpClient;
                if (currentClient is not null && !_subscribedClients.IsEmpty)
                {
                    DateTime now = DateTime.UtcNow;
                    DsuProtocol.BuildMotionDataPacket(packetBuffer, _serverId, currentPacketNum, in motion, _activeSlot, 0, _forwardGamepadInputs);
                    foreach (var kvp in _subscribedClients)
                    {
                        if (now - kvp.Value <= ClientSubscriptionTimeout)
                        {
                            await currentClient.SendAsync(packetBuffer, kvp.Key, cancellationToken);
                            Interlocked.Increment(ref _totalPacketsSent);
                        }
                        else
                        {
                            _subscribedClients.TryRemove(kvp.Key, out _);
                        }
                    }
                }

                UdpClient? secondaryClient = _secondaryClusterClient;
                if (_isSecondaryClusterActive && secondaryClient is not null && !_secondarySubscribedClients.IsEmpty)
                {
                    DateTime now = DateTime.UtcNow;
                    DsuProtocol.BuildMotionDataPacket(packetBuffer, _serverId, currentPacketNum, in motion, slot: 0, macSuffixOffset: 4, _forwardGamepadInputs);
                    foreach (var kvp in _secondarySubscribedClients)
                    {
                        if (now - kvp.Value <= ClientSubscriptionTimeout)
                        {
                            await secondaryClient.SendAsync(packetBuffer, kvp.Key, cancellationToken);
                            Interlocked.Increment(ref _totalPacketsSent);
                        }
                        else
                        {
                            _secondarySubscribedClients.TryRemove(kvp.Key, out _);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Keep broadcast loop alive on transient network errors
            }
        }
    }

    public void Dispose()
    {
        lock (_socketLock)
        {
            _udpClient?.Dispose();
            _udpClient = null;
            _secondaryClusterClient?.Dispose();
            _secondaryClusterClient = null;
        }
    }
}
