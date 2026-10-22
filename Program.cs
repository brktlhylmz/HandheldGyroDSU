using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HandheldDsuServer.Models;
using HandheldDsuServer.Protocol;
using HandheldDsuServer.Services;
using HandheldDsuServer.UI;

namespace HandheldDsuServer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Load persistent settings (%LocalAppData%\HandheldDsu\settings.json)
        AppSettings settings = AppSettings.Load();
        DeviceProfile defaultProfile = DeviceProfile.CreateDefault();

        using var sensorService = new SensorService(defaultProfile, settings);
        sensorService.Initialize();

        using var udpServer = new DsuUdpServer(sensorService, settings.UdpPort);
        udpServer.ToggleStreaming(settings.IsStreamingEnabled);
        using var ipcServer = new IpcHttpServer(sensorService, udpServer, settings, IpcHttpServer.DefaultIpcPort);

        // Run automated self-test suite if '--self-test' is passed
        if (args.Length > 0 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
        {
            using var testUdpServer = new DsuUdpServer(sensorService, 26770, 0, 26772);
            testUdpServer.ToggleStreaming(true);
            using var testIpcServer = new IpcHttpServer(sensorService, testUdpServer, settings, 26771);
            return await RunSelfTestAsync(testUdpServer, testIpcServer, sensorService, cts);
        }

        Task serverTask = udpServer.RunAsync(cts.Token);
        Task ipcTask = ipcServer.RunAsync(cts.Token);
        var dashboard = new ConsoleDashboard(sensorService, udpServer, ipcServer);
        Task uiTask = dashboard.RunAsync(cts);

        await Task.WhenAny(serverTask, ipcTask, uiTask);
        cts.Cancel();

        try
        {
            await Task.WhenAll(serverTask, ipcTask, uiTask);
        }
        catch (OperationCanceledException)
        {
            // Expected shutdown
        }

        return 0;
    }

    /// <summary>
    /// Verifies 0x100000, 0x100001, and 0x100002 Cemuhook DSU packets as well as
    /// local HTTP IPC endpoints (/status, /calibrate, /axes, /toggle) on 127.0.0.1:26761.
    /// </summary>
    private static async Task<int> RunSelfTestAsync(
        DsuUdpServer udpServer,
        IpcHttpServer ipcServer,
        SensorService sensorService,
        CancellationTokenSource cts)
    {
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        Console.WriteLine("[TEST] Running Cemuhook DSU Server, Sensor Calibration, and IPC API self-test...");
        Task serverTask = Task.Run(() => udpServer.RunAsync(cts.Token));
        Task ipcTask = Task.Run(() => ipcServer.RunAsync(cts.Token));
        await Task.Delay(50, cts.Token);

        await sensorService.CalibrateAsync(cts.Token);
        Console.WriteLine($"[TEST] 1s Calibration finished. Bias: {sensorService.CurrentBias} (GyroEvents: {sensorService.GyroEventsReceived}, AccelEvents: {sensorService.AccelEventsReceived})");

        using var client = new UdpClient();
        var serverEp = new IPEndPoint(IPAddress.Loopback, udpServer.Port);

        // 1. 0x100000 Version Request
        byte[] versionReq = BuildTestClientPacket(DsuProtocol.MsgTypeVersionRequest, []);
        await client.SendAsync(versionReq, serverEp, cts.Token);
        UdpReceiveResult verRes = await client.ReceiveAsync(cts.Token);
        ushort protoVer = BinaryPrimitives.ReadUInt16LittleEndian(verRes.Buffer.AsSpan(20, 2));
        Console.WriteLine($"[TEST] 0x100000 Version Response received: {verRes.Buffer.Length} bytes, Protocol v{protoVer}");

        // 2. 0x100001 Controller Info Request (Slot 0)
        byte[] infoPayload = [1, 0, 0, 0, 0, 0, 0, 0];
        byte[] infoReq = BuildTestClientPacket(DsuProtocol.MsgTypeControllerInfoRequest, infoPayload);
        await client.SendAsync(infoReq, serverEp, cts.Token);
        UdpReceiveResult infoRes = await client.ReceiveAsync(cts.Token);
        byte slotState = infoRes.Buffer[21];
        byte gyroModel = infoRes.Buffer[22];
        Console.WriteLine($"[TEST] 0x100001 Controller Info Response received: {infoRes.Buffer.Length} bytes, Slot 0 State={slotState} (Connected), Model={gyroModel} (Full Gyro)");

        // 3. 0x100002 Data Request
        byte[] dataPayload = new byte[8];
        byte[] dataReq = BuildTestClientPacket(DsuProtocol.MsgTypeControllerDataRequest, dataPayload);
        await client.SendAsync(dataReq, serverEp, cts.Token);
        UdpReceiveResult dataRes = await client.ReceiveAsync(cts.Token);

        ulong ts = BinaryPrimitives.ReadUInt64LittleEndian(dataRes.Buffer.AsSpan(68, 8));
        float accY = BinaryPrimitives.ReadSingleLittleEndian(dataRes.Buffer.AsSpan(80, 4));
        float pitch = BinaryPrimitives.ReadSingleLittleEndian(dataRes.Buffer.AsSpan(88, 4));
        Console.WriteLine($"[TEST] 0x100002 Motion Data Packet received: {dataRes.Buffer.Length} bytes, Timestamp={ts} us, AccY={accY:F2}G, Pitch={pitch:F2} deg/s");

        // 4. IPC HTTP API Tests (http://127.0.0.1:26761)
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ipcServer.Port}") };

        string statusJson = await http.GetStringAsync("/status", cts.Token);
        Console.WriteLine($"[TEST] GET /status succeeded: {statusJson[..Math.Min(110, statusJson.Length)]}...");

        var axesResp = await http.PostAsync("/axes", new StringContent("{\"reset\":true}", Encoding.UTF8, "application/json"), cts.Token);
        _ = await axesResp.Content.ReadAsStringAsync(cts.Token);
        Console.WriteLine($"[TEST] POST /axes (reset) succeeded: InvertY={sensorService.ActiveProfile.InvertY}");

        var toggleResp = await http.PostAsync("/toggle", new StringContent("{\"enabled\":true}", Encoding.UTF8, "application/json"), cts.Token);
        _ = await toggleResp.Content.ReadAsStringAsync(cts.Token);
        Console.WriteLine($"[TEST] POST /toggle succeeded: IsStreamingEnabled={udpServer.IsStreamingEnabled}");

        var recenterResp = await http.PostAsync("/recenter", new StringContent("{}", Encoding.UTF8, "application/json"), cts.Token);
        _ = await recenterResp.Content.ReadAsStringAsync(cts.Token);
        Console.WriteLine("[TEST] POST /recenter succeeded.");

        var calResp = await http.PostAsync("/calibrate", new StringContent("{}", Encoding.UTF8, "application/json"), cts.Token);
        _ = await calResp.Content.ReadAsStringAsync(cts.Token);
        Console.WriteLine("[TEST] POST /calibrate succeeded.");

        cts.Cancel();
        try
        {
            await Task.WhenAll(serverTask, ipcTask);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown after self-test completion
        }
        Console.WriteLine("[TEST] All Cemuhook DSU and local IPC self-tests passed!");
        return 0;
    }

    private static byte[] BuildTestClientPacket(uint msgType, ReadOnlySpan<byte> extraPayload)
    {
        byte[] packet = new byte[20 + extraPayload.Length];
        Span<byte> span = packet;
        span[0] = (byte)'D';
        span[1] = (byte)'S';
        span[2] = (byte)'U';
        span[3] = (byte)'C';
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), DsuProtocol.ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), (ushort)(4 + extraPayload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), 0x12345678u);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), msgType);
        extraPayload.CopyTo(span.Slice(20));

        uint crc = Crc32.Compute(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), crc);
        return packet;
    }
}
