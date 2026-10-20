using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HandheldDsuServer.Protocol;

namespace HandheldDsuWidget.Services;

/// <summary>
/// Built-in Cemuhook DSU v1001 UDP loopback client for Developer Options.
/// Subscribes to the local DSU server (primary and optional secondary cluster port)
/// to verify multi-slot controller discovery and 250 Hz motion streaming without an external emulator.
/// </summary>
public sealed class DsuLoopbackTesterService : IDisposable
{
    private const uint LoopbackClientId = 0xDEADBEEFu;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private UdpClient? _udpClient;

    private volatile int _primaryPort = 26760;
    private volatile int _secondaryPort = 26762;
    private volatile bool _includeSecondaryPort;

    private readonly bool[] _primarySlotsConnected = new bool[4];
    private readonly bool[] _secondarySlotsConnected = new bool[4];
    private long _packetsInCurrentWindow;
    private int _measuredPacketsPerSec;
    private long _totalPacketsReceived;

    public bool IsRunning { get; private set; }

    public void UpdateTargetPorts(int primaryPort, int secondaryPort, bool includeSecondaryPort)
    {
        _primaryPort = primaryPort;
        _secondaryPort = secondaryPort;
        _includeSecondaryPort = includeSecondaryPort;
    }

    public void Start(int primaryPort, int secondaryPort, bool includeSecondaryPort)
    {
        lock (_lock)
        {
            if (IsRunning)
                return;

            UpdateTargetPorts(primaryPort, secondaryPort, includeSecondaryPort);
            Array.Clear(_primarySlotsConnected);
            Array.Clear(_secondarySlotsConnected);
            _packetsInCurrentWindow = 0;
            _measuredPacketsPerSec = 0;
            _totalPacketsReceived = 0;

            _cts = new CancellationTokenSource();
            _udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            IsRunning = true;

            CancellationToken token = _cts.Token;
            _ = Task.Run(() => SubscriptionLoopAsync(token), token);
            _ = Task.Run(() => ReceiveLoopAsync(token), token);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!IsRunning)
                return;

            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            try { _udpClient?.Close(); } catch { }
            try { _udpClient?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }

            _udpClient = null;
            _cts = null;
            Array.Clear(_primarySlotsConnected);
            Array.Clear(_secondarySlotsConnected);
            _measuredPacketsPerSec = 0;
        }
    }

    public string GetDiagnosticSummary()
    {
        if (!IsRunning)
        {
            return LocalizationService.Instance["DevLoopbackIdleStatus"];
        }

        string primarySlots = FormatSlotList(_primarySlotsConnected);
        int hz = Volatile.Read(ref _measuredPacketsPerSec);
        long total = Interlocked.Read(ref _totalPacketsReceived);

        if (_includeSecondaryPort)
        {
            string secondarySlots = FormatSlotList(_secondarySlotsConnected);
            return FormattableString.Invariant(
                $"UDP {_primaryPort}: SLOTS [{primarySlots}]  |  UDP {_secondaryPort}: SLOTS [{secondarySlots}]  •  {hz} PKT/s ({total} TOT)");
        }

        return FormattableString.Invariant(
            $"UDP {_primaryPort}: SLOTS [{primarySlots}] ACTIVE  •  {hz} PKT/s ({total} RX)");
    }

    private static string FormatSlotList(bool[] slots)
    {
        var active = new List<int>(4);
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i])
                active.Add(i);
        }

        return active.Count > 0 ? string.Join(",", active) : "NONE";
    }

    private async Task SubscriptionLoopAsync(CancellationToken token)
    {
        byte[] infoReq = BuildControllerInfoRequest();
        byte[] dataReq = BuildControllerDataRequest();
        var rateWatch = Stopwatch.StartNew();

        try
        {
            while (!token.IsCancellationRequested)
            {
                UdpClient? client = _udpClient;
                if (client is null)
                    break;

                var primaryEp = new IPEndPoint(IPAddress.Loopback, _primaryPort);
                await client.SendAsync(infoReq, infoReq.Length, primaryEp);
                await client.SendAsync(dataReq, dataReq.Length, primaryEp);

                if (_includeSecondaryPort && _secondaryPort != _primaryPort)
                {
                    var secondaryEp = new IPEndPoint(IPAddress.Loopback, _secondaryPort);
                    await client.SendAsync(infoReq, infoReq.Length, secondaryEp);
                    await client.SendAsync(dataReq, dataReq.Length, secondaryEp);
                }

                await Task.Delay(1000, token);

                double elapsedSec = Math.Max(0.2, rateWatch.Elapsed.TotalSeconds);
                long count = Interlocked.Exchange(ref _packetsInCurrentWindow, 0);
                Volatile.Write(ref _measuredPacketsPerSec, (int)Math.Round(count / elapsedSec));
                rateWatch.Restart();
            }
        }
        catch
        {
            // Ignore cancellation or socket closure
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                UdpClient? client = _udpClient;
                if (client is null)
                    break;

                UdpReceiveResult res = await client.ReceiveAsync(token);
                byte[] buf = res.Buffer;
                if (buf.Length < 24)
                    continue;

                if (buf[0] != (byte)'D' || buf[1] != (byte)'S' || buf[2] != (byte)'U' || buf[3] != (byte)'S')
                    continue;

                uint msgType = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16, 4));
                byte slot = buf[20];
                byte state = buf[21];

                if (slot < 4)
                {
                    if (res.RemoteEndPoint.Port == _primaryPort)
                    {
                        _primarySlotsConnected[slot] = state == 2;
                    }
                    else if (res.RemoteEndPoint.Port == _secondaryPort)
                    {
                        _secondarySlotsConnected[slot] = state == 2;
                    }
                }

                if (msgType == DsuProtocol.MsgTypeControllerDataRequest)
                {
                    Interlocked.Increment(ref _packetsInCurrentWindow);
                    Interlocked.Increment(ref _totalPacketsReceived);
                }
            }
        }
        catch
        {
            // Ignore cancellation or socket closure
        }
    }

    private static byte[] BuildControllerInfoRequest()
    {
        // Header (16) + MsgType (4) + NumPorts (4) + Slots[4] (4) = 28 bytes
        byte[] packet = new byte[28];
        Span<byte> span = packet;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), DsuProtocol.MsgTypeControllerInfoRequest);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(20, 4), 4);
        span[24] = 0;
        span[25] = 1;
        span[26] = 2;
        span[27] = 3;
        FinalizeClientHeader(span);
        return packet;
    }

    private static byte[] BuildControllerDataRequest()
    {
        // Header (16) + MsgType (4) + RegFlags (1) + Slot (1) + Mac (6) = 28 bytes
        byte[] packet = new byte[28];
        Span<byte> span = packet;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), DsuProtocol.MsgTypeControllerDataRequest);
        span[20] = 0; // Subscribe to all controllers
        span[21] = 0;
        FinalizeClientHeader(span);
        return packet;
    }

    private static void FinalizeClientHeader(Span<byte> packet)
    {
        packet[0] = (byte)'D';
        packet[1] = (byte)'S';
        packet[2] = (byte)'U';
        packet[3] = (byte)'C';
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(4, 2), DsuProtocol.ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(6, 2), (ushort)(packet.Length - DsuProtocol.HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(8, 4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(12, 4), LoopbackClientId);

        uint crc = Crc32.Compute(packet);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(8, 4), crc);
    }

    public void Dispose()
    {
        Stop();
    }
}
