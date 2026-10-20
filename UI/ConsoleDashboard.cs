using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HandheldDsuServer.Models;
using HandheldDsuServer.Services;

namespace HandheldDsuServer.UI;

/// <summary>
/// Manages a flicker-free live console status dashboard and keyboard shortcuts.
/// </summary>
public sealed class ConsoleDashboard
{
    private readonly SensorService _sensorService;
    private readonly DsuUdpServer _udpServer;
    private readonly IpcHttpServer _ipcServer;

    public ConsoleDashboard(SensorService sensorService, DsuUdpServer udpServer, IpcHttpServer ipcServer)
    {
        _sensorService = sensorService;
        _udpServer = udpServer;
        _ipcServer = ipcServer;
    }

    public async Task RunAsync(CancellationTokenSource cts)
    {
        Console.OutputEncoding = Encoding.UTF8;
        bool isInteractive = !Console.IsOutputRedirected && !Console.IsInputRedirected;

        if (isInteractive)
        {
            try
            {
                Console.CursorVisible = false;
                Console.Clear();
            }
            catch
            {
                isInteractive = false;
            }
        }

        // Run an automatic 1-second calibration on startup
        Task initialCalTask = Task.Run(() => _sensorService.CalibrateAsync(cts.Token), cts.Token);

        if (!isInteractive)
        {
            await initialCalTask;
            RenderScreen(useCursorPositioning: false);

            using var daemonTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (!cts.Token.IsCancellationRequested && await daemonTimer.WaitForNextTickAsync(cts.Token))
            {
                MotionData m = _sensorService.GetProcessedMotionData();
                IReadOnlyList<IPEndPoint> clients = _udpServer.GetActiveClients();
                string clientInfo = clients.Count > 0 ? string.Join(", ", clients) : "Waiting";
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] UDP:{_udpServer.Port} | IPC:{_ipcServer.Port} | Stream:{(_udpServer.IsStreamingEnabled ? "ON" : "OFF")} | " +
                    $"Client:{clientInfo} | Profile:{_sensorService.ActiveProfile.Name} | " +
                    $"Gyro(P/Y/R): {m.GyroPitch,6:F2}, {m.GyroYaw,6:F2}, {m.GyroRoll,6:F2} deg/s");
            }
            return;
        }

        using var uiTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(100)); // 10 FPS refresh rate

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                HandleKeyboardInput(cts);
                RenderScreen(useCursorPositioning: true);

                if (!await uiTimer.WaitForNextTickAsync(cts.Token))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal exit
        }
        finally
        {
            try
            {
                Console.ResetColor();
                Console.CursorVisible = true;
            }
            catch
            {
                // Ignore console state errors
            }
        }
    }

    private void HandleKeyboardInput(CancellationTokenSource cts)
    {
        while (Console.KeyAvailable)
        {
            ConsoleKeyInfo keyInfo = Console.ReadKey(intercept: true);
            switch (char.ToUpperInvariant(keyInfo.KeyChar))
            {
                case 'C':
                    if (!_sensorService.IsCalibrating)
                    {
                        _ = Task.Run(() => _sensorService.CalibrateAsync(cts.Token), cts.Token);
                    }
                    break;

                case 'P':
                    _sensorService.SetOrCycleProfile();
                    break;

                case 'T':
                    _udpServer.ToggleStreaming();
                    break;

                case 'X':
                    _sensorService.ActiveProfile.InvertX = !_sensorService.ActiveProfile.InvertX;
                    break;

                case 'Y':
                    _sensorService.ActiveProfile.InvertY = !_sensorService.ActiveProfile.InvertY;
                    break;

                case 'Z':
                    _sensorService.ActiveProfile.InvertZ = !_sensorService.ActiveProfile.InvertZ;
                    break;

                case 'S':
                    _sensorService.ActiveProfile.SwapAxes = !_sensorService.ActiveProfile.SwapAxes;
                    break;

                case 'Q':
                    cts.Cancel();
                    break;
            }
        }
    }

    private void RenderScreen(bool useCursorPositioning)
    {
        if (useCursorPositioning)
        {
            try
            {
                Console.SetCursorPosition(0, 0);
            }
            catch
            {
                // Ignore cursor positioning errors
            }
        }

        MotionData motion = _sensorService.GetProcessedMotionData();
        GyroBias bias = _sensorService.CurrentBias;
        DeviceProfile profile = _sensorService.ActiveProfile;
        IReadOnlyList<IPEndPoint> activeClients = _udpServer.GetActiveClients();

        WriteColoredLine("======================================================================", ConsoleColor.DarkCyan);
        WriteColoredLine("   HANDHELD CEMUHOOK DSU (GYRO/MOTION) SERVER - .NET 10 (WinRT)       ", ConsoleColor.Cyan);
        WriteColoredLine("   Universal Windows Handheld IMU Server + Xbox Game Bar IPC          ", ConsoleColor.DarkGray);
        WriteColoredLine("======================================================================", ConsoleColor.DarkCyan);

        // 1. Server and IPC Status
        WriteLabelValue("  UDP Server Address: ", $"0.0.0.0:{_udpServer.Port} (DSUS v1001 | ID: 0x{_udpServer.ServerId:X8})", ConsoleColor.Green);
        WriteLabelValue("  Game Bar IPC API  : ", $"http://127.0.0.1:{_ipcServer.Port} (/status, /calibrate, /profile, /toggle)", ConsoleColor.Cyan);
        WriteLabelValue(
            "  DSU Data Stream   : ",
            _udpServer.IsStreamingEnabled ? "ENABLED (Active Broadcast)" : "PAUSED (Disabled)",
            _udpServer.IsStreamingEnabled ? ConsoleColor.Green : ConsoleColor.Red);

        if (activeClients.Count > 0)
        {
            string clientsText = string.Join(", ", activeClients);
            WriteLabelValue("  Connected Clients : ", $"{clientsText} (Packets: {_udpServer.TotalPacketsSent:N0})", ConsoleColor.Green);
        }
        else
        {
            WriteLabelValue("  Connected Clients : ", "Waiting... (Cemu/Eden/Yuzu/Ryujinx/Dolphin: 127.0.0.1:26760)", ConsoleColor.Yellow);
        }

        // 2. Hardware Sensor Status
        string gyroStatus = _sensorService.IsGyroAvailable
            ? $"Active ({_sensorService.GyroReportIntervalMs} ms / ~{1000 / Math.Max(1u, _sensorService.GyroReportIntervalMs)} Hz)"
            : "Not Found (Fallback Mode)";

        string accelStatus = _sensorService.IsAccelerometerAvailable
            ? $"Active ({_sensorService.AccelReportIntervalMs} ms / ~{1000 / Math.Max(1u, _sensorService.AccelReportIntervalMs)} Hz)"
            : "Not Found (Fallback Mode)";

        WriteLabelValue("  Gyrometer (WinRT) : ", gyroStatus, _sensorService.IsGyroAvailable ? ConsoleColor.Green : ConsoleColor.Red);
        WriteLabelValue("  Accelerometer     : ", accelStatus, _sensorService.IsAccelerometerAvailable ? ConsoleColor.Green : ConsoleColor.Red);

        WriteColoredLine("----------------------------------------------------------------------", ConsoleColor.DarkGray);

        // 3. Device Profile and Axis Orientation
        WriteLabelValue("  Active Profile    : ", profile.Name, ConsoleColor.Magenta);
        WriteLabelValue(
            "  Axis Orientation  : ",
            $"InvertX: {(profile.InvertX ? "ON " : "OFF")} | InvertY: {(profile.InvertY ? "ON " : "OFF")} | InvertZ: {(profile.InvertZ ? "ON " : "OFF")} | SwapAxes: {(profile.SwapAxes ? "ON " : "OFF")}",
            ConsoleColor.White);

        WriteColoredLine("----------------------------------------------------------------------", ConsoleColor.DarkGray);

        // 4. Live Motion Telemetry
        WriteColoredLine("  [LIVE GYROSCOPE TELEMETRY (deg/s)]", ConsoleColor.Cyan);
        WriteLabelValue("    Pitch (X) : ", $"{motion.GyroPitch,9:F2} deg/s", ConsoleColor.Yellow);
        WriteLabelValue("    Yaw   (Z) : ", $"{motion.GyroYaw,9:F2} deg/s", ConsoleColor.Yellow);
        WriteLabelValue("    Roll  (Y) : ", $"{motion.GyroRoll,9:F2} deg/s", ConsoleColor.Yellow);

        WriteColoredLine("  [LIVE ACCELEROMETER TELEMETRY (G)]", ConsoleColor.Cyan);
        WriteLabelValue("    Acc X     : ", $"{motion.AccX,9:F3} G", ConsoleColor.White);
        WriteLabelValue("    Acc Y     : ", $"{motion.AccY,9:F3} G", ConsoleColor.White);
        WriteLabelValue("    Acc Z     : ", $"{motion.AccZ,9:F3} G", ConsoleColor.White);

        WriteColoredLine("----------------------------------------------------------------------", ConsoleColor.DarkGray);

        // 5. Calibration Status
        if (_sensorService.IsCalibrating)
        {
            WriteLabelValue(
                "  Calibration State : ",
                $"CALIBRATING... Keep device stationary ({_sensorService.CalibrationProgressPercent}%)",
                ConsoleColor.Yellow);
        }
        else
        {
            WriteLabelValue(
                "  Drift Bias Offset : ",
                $"X: {bias.BiasX:+0.00;-0.00;0.00} | Y: {bias.BiasY:+0.00;-0.00;0.00} | Z: {bias.BiasZ:+0.00;-0.00;0.00} deg/s",
                ConsoleColor.Green);
        }

        WriteColoredLine("======================================================================", ConsoleColor.DarkCyan);
        WriteColoredLine("  [C] Calibrate (Hold stationary for 1s)                              ", ConsoleColor.Green);
        WriteColoredLine("  [P] Cycle Device Profile (Standard -> Portrait -> Alternative)      ", ConsoleColor.Gray);
        WriteColoredLine("  [T] Toggle Stream | [X/Y/Z] Invert Axis | [S] Swap Axes | [Q] Quit  ", ConsoleColor.Gray);
        WriteColoredLine("======================================================================", ConsoleColor.DarkCyan);
        Console.ResetColor();
    }

    private static void WriteColoredLine(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text.PadRight(72));
    }

    private static void WriteLabelValue(string label, string value, ConsoleColor valueColor)
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.Write(label);
        Console.ForegroundColor = valueColor;
        int remaining = Math.Max(0, 72 - label.Length);
        Console.WriteLine(value.PadRight(remaining));
    }
}
