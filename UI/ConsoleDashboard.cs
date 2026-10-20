using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using HandheldDsuServer.Models;
using HandheldDsuServer.Services;

namespace HandheldDsuServer.UI;

/// <summary>
/// Flicker-free real-time console dashboard displaying sensor telemetry,
/// connected emulator clients, axis orientation, and keyboard shortcuts.
/// </summary>
public sealed class ConsoleDashboard
{
    private readonly SensorService _sensorService;
    private readonly DsuUdpServer _udpServer;
    private readonly IpcHttpServer _ipcServer;

    public ConsoleDashboard(
        SensorService sensorService,
        DsuUdpServer udpServer,
        IpcHttpServer ipcServer)
    {
        _sensorService = sensorService;
        _udpServer = udpServer;
        _ipcServer = ipcServer;
    }

    public async Task RunAsync(CancellationTokenSource appCts)
    {
        CancellationToken token = appCts.Token;
        bool hasConsole = !Console.IsOutputRedirected && !Console.IsInputRedirected;

        if (hasConsole)
        {
            try
            {
                Console.CursorVisible = false;
                Console.Clear();
            }
            catch
            {
                hasConsole = false;
            }
        }

        // Start an automatic 1-second calibration on startup if no prior bias offsets were saved
        GyroBias initialBias = _sensorService.CurrentBias;
        if (initialBias.BiasX == 0f && initialBias.BiasY == 0f && initialBias.BiasZ == 0f)
        {
            _ = Task.Run(async () =>
            {
                await _sensorService.CalibrateAsync(token);
                _ipcServer.SaveCurrentSettings();
            }, token);
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (hasConsole)
                {
                    HandleKeyboardShortcuts(appCts);
                    RenderFrame();
                }

                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal exit
        }
        finally
        {
            if (hasConsole)
            {
                try
                {
                    Console.ResetColor();
                    Console.CursorVisible = true;
                    Console.WriteLine();
                    Console.WriteLine("[INFO] Handheld Cemuhook DSU Server stopped.");
                }
                catch
                {
                    // Ignore console reset errors
                }
            }
        }
    }

    private void HandleKeyboardShortcuts(CancellationTokenSource appCts)
    {
        while (Console.KeyAvailable)
        {
            ConsoleKeyInfo keyInfo = Console.ReadKey(intercept: true);
            switch (keyInfo.Key)
            {
                case ConsoleKey.C:
                    if (!_sensorService.IsCalibrating)
                    {
                        _ = Task.Run(async () =>
                        {
                            await _sensorService.CalibrateAsync(appCts.Token);
                            _ipcServer.SaveCurrentSettings();
                        }, appCts.Token);
                    }
                    break;

                case ConsoleKey.R:
                    _sensorService.ResetAxisSettings();
                    _ipcServer.SaveCurrentSettings();
                    break;

                case ConsoleKey.T:
                    _udpServer.ToggleStreaming();
                    _ipcServer.SaveCurrentSettings();
                    break;

                case ConsoleKey.X:
                    _sensorService.ActiveProfile.InvertX = !_sensorService.ActiveProfile.InvertX;
                    _ipcServer.SaveCurrentSettings();
                    break;

                case ConsoleKey.Y:
                    _sensorService.ActiveProfile.InvertY = !_sensorService.ActiveProfile.InvertY;
                    _ipcServer.SaveCurrentSettings();
                    break;

                case ConsoleKey.Z:
                    _sensorService.ActiveProfile.InvertZ = !_sensorService.ActiveProfile.InvertZ;
                    _ipcServer.SaveCurrentSettings();
                    break;

                case ConsoleKey.S:
                    _sensorService.ActiveProfile.SwapAxes = !_sensorService.ActiveProfile.SwapAxes;
                    _ipcServer.SaveCurrentSettings();
                    break;

                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    appCts.Cancel();
                    return;
            }
        }
    }

    private void RenderFrame()
    {
        try
        {
            Console.SetCursorPosition(0, 0);
        }
        catch
        {
            return;
        }

        MotionData motion = _sensorService.GetProcessedMotionData();
        GyroBias bias = _sensorService.CurrentBias;
        DeviceProfile profile = _sensorService.ActiveProfile;
        IReadOnlyList<IPEndPoint> activeClients = _udpServer.GetActiveClients();

        WriteColoredLine("======================================================================", ConsoleColor.DarkCyan);
        WriteColoredLine("   HANDHELD CEMUHOOK DSU (GYRO/MOTION) SERVER - .NET 10 (WinRT)       ", ConsoleColor.Cyan);
        WriteColoredLine("   Universal Windows Handheld IMU Server + Local HTTP IPC             ", ConsoleColor.DarkGray);
        WriteColoredLine("======================================================================", ConsoleColor.DarkCyan);

        // 1. Server and IPC Status
        WriteLabelValue("  UDP Server Address: ", $"0.0.0.0:{_udpServer.Port} (DSUS v1001 | ID: 0x{_udpServer.ServerId:X8})", ConsoleColor.Green);
        WriteLabelValue("  Local IPC API     : ", $"http://127.0.0.1:{_ipcServer.Port} (/status, /calibrate, /axes, /toggle)", ConsoleColor.Cyan);
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

        // 3. Axis Orientation
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
        WriteColoredLine("  [C] Calibrate (Hold stationary for 1s) | [R] Reset Axis Defaults    ", ConsoleColor.Green);
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
