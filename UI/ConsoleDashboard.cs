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
/// Konsolda renkli, titreşimsiz (flicker-free) canlı durum ekranı ve klavye kısayollarını yönetir.
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

        // Başlangıçta otomatik 1 saniyelik kalibrasyon başlat
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
                string clientInfo = clients.Count > 0 ? string.Join(", ", clients) : "Bekleniyor";
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] UDP:{_udpServer.Port} | IPC:{_ipcServer.Port} | Akış:{(_udpServer.IsStreamingEnabled ? "AÇIK" : "KAPALI")} | " +
                    $"İstemci:{clientInfo} | Profil:{_sensorService.ActiveProfile.Name} | " +
                    $"Gyro(P/Y/R): {m.GyroPitch,6:F2}, {m.GyroYaw,6:F2}, {m.GyroRoll,6:F2} deg/s");
            }
            return;
        }

        using var uiTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(100)); // 10 FPS ekran yenileme

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
            // Normal çıkış
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
                // Yoksay
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
                // Yoksay
            }
        }

        MotionData motion = _sensorService.GetProcessedMotionData();
        GyroBias bias = _sensorService.CurrentBias;
        DeviceProfile profile = _sensorService.ActiveProfile;
        IReadOnlyList<IPEndPoint> activeClients = _udpServer.GetActiveClients();

        WriteColoredLine("======================================================================", ConsoleColor.DarkCyan);
        WriteColoredLine("   HANDHELD CEMUHOOK DSU (GYRO/MOTION) SUNUCUSU - .NET 10 (WinRT)     ", ConsoleColor.Cyan);
        WriteColoredLine("   Asus ROG Ally | Lenovo Legion Go | MSI Claw + Xbox Game Bar IPC    ", ConsoleColor.DarkGray);
        WriteColoredLine("======================================================================", ConsoleColor.DarkCyan);

        // 1. Sunucu ve IPC Durumu
        WriteLabelValue("  UDP Sunucu Adresi : ", $"0.0.0.0:{_udpServer.Port} (DSUS v1001 | ID: 0x{_udpServer.ServerId:X8})", ConsoleColor.Green);
        WriteLabelValue("  Game Bar IPC API  : ", $"http://127.0.0.1:{_ipcServer.Port} (/status, /calibrate, /profile, /toggle)", ConsoleColor.Cyan);
        WriteLabelValue(
            "  DSU Veri Akışı    : ",
            _udpServer.IsStreamingEnabled ? "AÇIK (Aktif Yayın)" : "DURAKLATILDI (Kapalı)",
            _udpServer.IsStreamingEnabled ? ConsoleColor.Green : ConsoleColor.Red);

        if (activeClients.Count > 0)
        {
            string clientsText = string.Join(", ", activeClients);
            WriteLabelValue("  Bağlı Emülatör IP : ", $"{clientsText} (Paket: {_udpServer.TotalPacketsSent:N0})", ConsoleColor.Green);
        }
        else
        {
            WriteLabelValue("  Bağlı Emülatör IP : ", "Bekleniyor... (Cemu/Yuzu/Ryujinx/Dolphin: 127.0.0.1:26760)", ConsoleColor.Yellow);
        }

        // 2. Donanım / Sensör Durumu
        string gyroStatus = _sensorService.IsGyroAvailable
            ? $"Aktif ({_sensorService.GyroReportIntervalMs} ms / ~{1000 / Math.Max(1u, _sensorService.GyroReportIntervalMs)} Hz)"
            : "Bulunamadı (Simülasyon / Sabit Mod)";

        string accelStatus = _sensorService.IsAccelerometerAvailable
            ? $"Aktif ({_sensorService.AccelReportIntervalMs} ms / ~{1000 / Math.Max(1u, _sensorService.AccelReportIntervalMs)} Hz)"
            : "Bulunamadı (Simülasyon / Sabit Mod)";

        WriteLabelValue("  Gyrometer (WinRT) : ", gyroStatus, _sensorService.IsGyroAvailable ? ConsoleColor.Green : ConsoleColor.Red);
        WriteLabelValue("  Accelerometer     : ", accelStatus, _sensorService.IsAccelerometerAvailable ? ConsoleColor.Green : ConsoleColor.Red);

        WriteColoredLine("----------------------------------------------------------------------", ConsoleColor.DarkGray);

        // 3. Cihaz Profili ve Eksen Ayarları
        WriteLabelValue("  Aktif Cihaz Profil: ", profile.Name, ConsoleColor.Magenta);
        WriteLabelValue(
            "  Eksen Ayarları    : ",
            $"InvertX: {(profile.InvertX ? "AÇIK " : "KAPALI")} | InvertY: {(profile.InvertY ? "AÇIK " : "KAPALI")} | InvertZ: {(profile.InvertZ ? "AÇIK " : "KAPALI")} | SwapAxes: {(profile.SwapAxes ? "AÇIK " : "KAPALI")}",
            ConsoleColor.White);

        WriteColoredLine("----------------------------------------------------------------------", ConsoleColor.DarkGray);

        // 4. Anlık Hareket Verileri
        WriteColoredLine("  [ANLIK JİROSKOP VERİLERİ (deg/s)]", ConsoleColor.Cyan);
        WriteLabelValue("    Pitch (X) : ", $"{motion.GyroPitch,9:F2} deg/s", ConsoleColor.Yellow);
        WriteLabelValue("    Yaw   (Z) : ", $"{motion.GyroYaw,9:F2} deg/s", ConsoleColor.Yellow);
        WriteLabelValue("    Roll  (Y) : ", $"{motion.GyroRoll,9:F2} deg/s", ConsoleColor.Yellow);

        WriteColoredLine("  [ANLIK İVMEÖLÇER VERİLERİ (G)]", ConsoleColor.Cyan);
        WriteLabelValue("    Acc X     : ", $"{motion.AccX,9:F3} G", ConsoleColor.White);
        WriteLabelValue("    Acc Y     : ", $"{motion.AccY,9:F3} G", ConsoleColor.White);
        WriteLabelValue("    Acc Z     : ", $"{motion.AccZ,9:F3} G", ConsoleColor.White);

        WriteColoredLine("----------------------------------------------------------------------", ConsoleColor.DarkGray);

        // 5. Kalibrasyon Durumu
        if (_sensorService.IsCalibrating)
        {
            WriteLabelValue(
                "  Kalibrasyon Durumu: ",
                $"KALİBRE EDİLİYOR... Lütfen cihazı sabit tutun (%{_sensorService.CalibrationProgressPercent})",
                ConsoleColor.Yellow);
        }
        else
        {
            WriteLabelValue(
                "  Drift Bias Sapması: ",
                $"X: {bias.BiasX:+0.00;-0.00;0.00} | Y: {bias.BiasY:+0.00;-0.00;0.00} | Z: {bias.BiasZ:+0.00;-0.00;0.00} deg/s",
                ConsoleColor.Green);
        }

        WriteColoredLine("======================================================================", ConsoleColor.DarkCyan);
        WriteColoredLine("  [C] Kalibrasyon için 'C' tuşuna bas (Cihazı 1 sn düz ve sabit tutun)", ConsoleColor.Green);
        WriteColoredLine("  [P] Cihaz Profili Değiştir (ROG Ally -> Legion Go -> MSI Claw)      ", ConsoleColor.Gray);
        WriteColoredLine("  [T] Akışı Aç/Kapat | [X/Y/Z] Eksen Ters Çevir | [S] Swap | [Q] Çıkış", ConsoleColor.Gray);
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
