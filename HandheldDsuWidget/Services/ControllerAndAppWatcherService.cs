using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Windows.Gaming.Input;
using HandheldDsuServer.Models;

namespace HandheldDsuWidget.Services;

/// <summary>
/// Monitors connected physical controllers (via VID:PID) and active foreground emulators/games (.exe),
/// automatically managing DSU multi-slot / multi-port assignment and profile switching.
/// </summary>
public sealed class ControllerAndAppWatcherService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private readonly DispatcherTimer _watcherTimer;
    private readonly object _syncLock = new();
    private AutomationConfig _config;
    private string _lastDetectedEmulatorProcess = string.Empty;
    private HashSet<string> _previousConnectedHwIds = [];
    private bool _isInitialScan = true;

    public AutomationConfig Config => _config;

    public event EventHandler<(string Title, string Subtitle, string? ProfileToApply, int ConnectedCount)>? AutomationNotification;
    public event EventHandler? ControllersChanged;

    public ControllerAndAppWatcherService()
    {
        _config = AutomationConfig.Load();

        _watcherTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _watcherTimer.Tick += WatcherTimer_Tick;

        RawGameController.RawGameControllerAdded += OnRawControllerChanged;
        RawGameController.RawGameControllerRemoved += OnRawControllerChanged;
    }

    public void Start()
    {
        ScanControllers(basePort: 26760);
        _isInitialScan = false;
        _watcherTimer.Start();
    }

    public void SaveConfig()
    {
        lock (_syncLock)
        {
            _config.Save();
        }
    }

    public void ForgetController(string hardwareId)
    {
        lock (_syncLock)
        {
            _config.Controllers.RemoveAll(c =>
                string.Equals(c.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase) && !c.IsConnected);
            _config.Save();
        }
        ControllersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddOrUpdateEmulatorRule(string processName, string displayName, string profileName, bool isEnabled)
    {
        string cleanProc = processName.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cleanProc))
            return;

        lock (_syncLock)
        {
            var existing = _config.EmulatorRules.FirstOrDefault(r =>
                string.Equals(r.ProcessName, cleanProc, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.ProfileName = profileName;
                existing.IsEnabled = isEnabled;
                if (!string.IsNullOrWhiteSpace(displayName))
                    existing.DisplayName = displayName;
            }
            else
            {
                _config.EmulatorRules.Add(new EmulatorProfileRule
                {
                    ProcessName = cleanProc,
                    DisplayName = string.IsNullOrWhiteSpace(displayName)
                        ? AutomationConfig.ResolveFriendlyProcessDisplayName(cleanProc)
                        : displayName,
                    ProfileName = profileName,
                    IsEnabled = isEnabled
                });
            }

            _config.Save();
        }
    }

    public void RemoveEmulatorRule(string processName)
    {
        lock (_syncLock)
        {
            _config.EmulatorRules.RemoveAll(r =>
                string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            _config.Save();
        }
        ControllersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRawControllerChanged(object? sender, RawGameController e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ScanControllers(basePort: 26760);
        });
    }

    private void WatcherTimer_Tick(object? sender, EventArgs e)
    {
        ScanControllers(basePort: 26760);
        CheckForegroundEmulator();
    }

    public IReadOnlyList<KnownControllerConfig> ScanControllers(int basePort = 26760, int secondaryPort = 26762)
    {
        lock (_syncLock)
        {
            var rawSnapshot = RawGameController.RawGameControllers.ToList();
            var currentConnectedHwIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in _config.Controllers)
            {
                c.IsConnected = false;
            }

            // Ensure built-in handheld sensor entry always exists as primary fallback if no external pads are found
            int playerIndex = 0;

            foreach (RawGameController raw in rawSnapshot)
            {
                string hwId = $"VID_{raw.HardwareVendorId:X4}&PID_{raw.HardwareProductId:X4}";
                // Distinguish multiple identical controllers by index if needed
                string uniqueKey = hwId;
                int suffix = 2;
                while (!currentConnectedHwIds.Add(uniqueKey))
                {
                    uniqueKey = $"{hwId}#{suffix++}";
                }

                var known = _config.Controllers.FirstOrDefault(c =>
                    string.Equals(c.HardwareId, uniqueKey, StringComparison.OrdinalIgnoreCase));

                string friendlyName = ResolveFriendlyControllerName(raw.HardwareVendorId, raw.HardwareProductId, raw.DisplayName);

                if (known is null)
                {
                    known = new KnownControllerConfig
                    {
                        HardwareId = uniqueKey,
                        DisplayName = friendlyName,
                        AssignedProfileName = "Standard Landscape",
                        AutoSwitchProfile = true,
                        LastSeenUtc = DateTime.UtcNow
                    };
                    _config.Controllers.Add(known);
                }
                else if (!string.IsNullOrWhiteSpace(friendlyName))
                {
                    known.DisplayName = friendlyName;
                }

                known.IsConnected = true;
                known.LastSeenUtc = DateTime.UtcNow;
                known.AssignedPlayerIndex = playerIndex;
                known.AssignedUdpPort = playerIndex < 4 ? basePort : secondaryPort;
                known.AssignedDsuSlot = playerIndex % 4;

                if (!_isInitialScan && !_previousConnectedHwIds.Contains(uniqueKey))
                {
                    string? profileToApply = (_config.AutoSwitchByController && known.AutoSwitchProfile)
                        ? known.AssignedProfileName
                        : null;

                    var loc = LocalizationService.Instance;
                    AutomationNotification?.Invoke(
                        this,
                        (
                            loc.Format("ToastControllerConnectedTitleFormat", known.DisplayName),
                            loc.Format("ToastControllerConnectedSubFormat", playerIndex + 1, known.AssignedUdpPort, known.AssignedDsuSlot, known.AssignedProfileName),
                            profileToApply,
                            rawSnapshot.Count
                        ));
                }

                playerIndex++;
            }

            if (rawSnapshot.Count == 0)
            {
                const string builtInId = "BUILTIN_HANDHELD_IMU";
                currentConnectedHwIds.Add(builtInId);
                var builtIn = _config.Controllers.FirstOrDefault(c => c.HardwareId == builtInId);
                if (builtIn is null)
                {
                    builtIn = new KnownControllerConfig
                    {
                        HardwareId = builtInId,
                        DisplayName = "Handheld Built-in IMU Controller",
                        AssignedProfileName = "Standard Landscape",
                        AutoSwitchProfile = true,
                        LastSeenUtc = DateTime.UtcNow
                    };
                    _config.Controllers.Insert(0, builtIn);
                }
                builtIn.IsConnected = true;
                builtIn.AssignedPlayerIndex = 0;
                builtIn.AssignedUdpPort = basePort;
                builtIn.AssignedDsuSlot = 0;
            }

            bool changed = !_previousConnectedHwIds.SetEquals(currentConnectedHwIds);
            _previousConnectedHwIds = currentConnectedHwIds;

            if (changed)
            {
                _config.Save();
                ControllersChanged?.Invoke(this, EventArgs.Empty);
            }

            return _config.Controllers
                .OrderByDescending(c => c.IsConnected)
                .ThenBy(c => c.AssignedPlayerIndex)
                .ThenByDescending(c => c.LastSeenUtc)
                .ToList();
        }
    }

    private void CheckForegroundEmulator()
    {
        if (!_config.AutoSwitchByEmulator)
            return;

        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
                return;

            using Process proc = Process.GetProcessById((int)pid);
            string procName = proc.ProcessName.ToLowerInvariant();

            if (string.Equals(procName, _lastDetectedEmulatorProcess, StringComparison.OrdinalIgnoreCase))
                return;

            var matchedRule = _config.EmulatorRules.FirstOrDefault(r =>
                r.IsEnabled && procName.Contains(r.ProcessName, StringComparison.OrdinalIgnoreCase));

            if (matchedRule is not null)
            {
                _lastDetectedEmulatorProcess = procName;
                int connectedCount = _config.Controllers.Count(c => c.IsConnected);
                var loc = LocalizationService.Instance;
                AutomationNotification?.Invoke(
                    this,
                    (
                        loc.Format("ToastEmulatorDetectedTitleFormat", matchedRule.DisplayName),
                        loc.Format("ToastEmulatorDetectedSubFormat", matchedRule.ProfileName),
                        matchedRule.ProfileName,
                        connectedCount
                    ));
            }
        }
        catch
        {
            // Ignore access denied on elevated system processes
        }
    }

    private static string ResolveFriendlyControllerName(ushort vid, ushort pid, string? rawDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(rawDisplayName) &&
            !rawDisplayName.Equals("Controller", StringComparison.OrdinalIgnoreCase) &&
            !rawDisplayName.Equals("Gamepad", StringComparison.OrdinalIgnoreCase))
        {
            return rawDisplayName.Trim();
        }

        return (vid, pid) switch
        {
            (0x0B05, _) => "ASUS ROG Ally Controller",
            (0x17EF, _) => "Lenovo Legion Go Controller",
            (0x054C, 0x0CE6) => "Sony DualSense Wireless Controller",
            (0x054C, 0x0DF2) => "Sony DualSense Edge Controller",
            (0x054C, _) => "Sony DualShock 4 Controller",
            (0x057E, 0x2009) => "Nintendo Switch Pro Controller",
            (0x2DC8, _) => "8BitDo Wireless Controller",
            (0x3537, _) => "GameSir Controller",
            (0x045E, _) => "Xbox / XInput Controller",
            _ => $"Gamepad ({vid:X4}:{pid:X4})"
        };
    }

    public void Dispose()
    {
        _watcherTimer.Stop();
        RawGameController.RawGameControllerAdded -= OnRawControllerChanged;
        RawGameController.RawGameControllerRemoved -= OnRawControllerChanged;
    }
}
