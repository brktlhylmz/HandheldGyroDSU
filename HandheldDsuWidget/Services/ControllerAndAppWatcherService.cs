using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
    private readonly List<(string HwId, string DisplayName, string DefaultProfile)> _simulatedControllers = [];
    private AutomationConfig _config;
    private string _lastDetectedEmulatorProcess = string.Empty;
    private HashSet<string> _previousConnectedHwIds = [];
    private bool _isInitialScan = true;
    private bool _isScanning;

    public AutomationConfig Config => _config;
    public int SimulatedControllerCount
    {
        get { lock (_syncLock) return _simulatedControllers.Count; }
    }

    public event EventHandler<(string Title, string Subtitle, string? ProfileToApply, int ConnectedCount)>? AutomationNotification;
    public event EventHandler? ControllersChanged;

    public ControllerAndAppWatcherService()
    {
        _config = AutomationConfig.Load();
        _config.Controllers.RemoveAll(c => c.HardwareId.StartsWith("SIM_VID_", StringComparison.OrdinalIgnoreCase));

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
        _ = Task.Run(() => EnsureDolphinDsuConfigured());
        ScanControllers(basePort: 26760);
        _isInitialScan = false;
        _watcherTimer.Start();
    }

    public IReadOnlyList<KnownControllerConfig> GetControllersSnapshot()
    {
        lock (_syncLock)
        {
            return _config.Controllers
                .OrderByDescending(c => c.IsConnected)
                .ThenBy(c => c.AssignedPlayerIndex)
                .ThenByDescending(c => c.LastSeenUtc)
                .ToList();
        }
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
            _simulatedControllers.RemoveAll(s => string.Equals(s.HwId, hardwareId, StringComparison.OrdinalIgnoreCase));
            _config.Controllers.RemoveAll(c =>
                string.Equals(c.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase) && !c.IsConnected);
            _config.Save();
        }
        ControllersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SimulateVirtualControllerPreset(string presetKey, int basePort = 26760, int secondaryPort = 26762)
    {
        lock (_syncLock)
        {
            switch (presetKey.ToLowerInvariant())
            {
                case "dualsense":
                    AddSimulatedEntry("SIM_VID_054C&PID_0CE6", "Sony DualSense Wireless Controller (SIM)", "Standard Landscape");
                    break;

                case "switchpro":
                    AddSimulatedEntry("SIM_VID_057E&PID_2009", "Nintendo Switch Pro Controller (SIM)", "Standard Landscape");
                    break;

                case "legiongo":
                    AddSimulatedEntry("SIM_VID_17EF&PID_6182", "Lenovo Legion Go Controller (SIM)", "Portrait Sensor (Legion Go)");
                    break;

                case "cluster4":
                    _simulatedControllers.Clear();
                    AddSimulatedEntry("SIM_VID_054C&PID_0CE6", "Sony DualSense Wireless Controller (SIM)", "Standard Landscape");
                    AddSimulatedEntry("SIM_VID_057E&PID_2009", "Nintendo Switch Pro Controller (SIM)", "Standard Landscape");
                    AddSimulatedEntry("SIM_VID_054C&PID_09CC", "Sony DualShock 4 Wireless Controller (SIM)", "Standard Landscape");
                    break;

                case "cluster8":
                    _simulatedControllers.Clear();
                    AddSimulatedEntry("SIM_VID_054C&PID_0CE6", "Sony DualSense Wireless Controller #1 (SIM)", "Standard Landscape");
                    AddSimulatedEntry("SIM_VID_054C&PID_0CE6#2", "Sony DualSense Wireless Controller #2 (SIM)", "Standard Landscape");
                    AddSimulatedEntry("SIM_VID_057E&PID_2009", "Nintendo Switch Pro Controller #1 (SIM)", "Standard Landscape");
                    AddSimulatedEntry("SIM_VID_057E&PID_2009#2", "Nintendo Switch Pro Controller #2 (SIM)", "Standard Landscape");
                    AddSimulatedEntry("SIM_VID_2DC8&PID_3106", "8BitDo Motion Controller (SIM)", "Standard Landscape");
                    AddSimulatedEntry("SIM_VID_3537&PID_1004", "GameSir Motion Controller (SIM)", "Standard Landscape");
                    AddSimulatedEntry("SIM_VID_045E&PID_0B12", "Xbox Wireless Controller #2 (SIM)", "Standard Landscape");
                    break;
            }
        }

        ScanControllers(basePort, secondaryPort);
    }

    public void ClearSimulatedControllers(int basePort = 26760, int secondaryPort = 26762)
    {
        lock (_syncLock)
        {
            _simulatedControllers.Clear();
            _config.Controllers.RemoveAll(c => c.HardwareId.StartsWith("SIM_VID_", StringComparison.OrdinalIgnoreCase));
            _config.Save();
        }

        ScanControllers(basePort, secondaryPort);
    }

    private void AddSimulatedEntry(string baseHwId, string displayName, string defaultProfile)
    {
        string uniqueKey = baseHwId;
        int suffix = 2;
        while (_simulatedControllers.Any(s => string.Equals(s.HwId, uniqueKey, StringComparison.OrdinalIgnoreCase)))
        {
            uniqueKey = $"{baseHwId}#{suffix++}";
        }

        _simulatedControllers.Add((uniqueKey, displayName, defaultProfile));
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
        List<(string Title, string Subtitle, string? ProfileToApply, int ConnectedCount)> pendingNotifications = [];
        bool changed = false;
        List<KnownControllerConfig> orderedSnapshot;

        lock (_syncLock)
        {
            if (_isScanning)
            {
                return _config.Controllers
                    .OrderByDescending(c => c.IsConnected)
                    .ThenBy(c => c.AssignedPlayerIndex)
                    .ThenByDescending(c => c.LastSeenUtc)
                    .ToList();
            }

            _isScanning = true;
            try
            {
                List<RawGameController> rawSnapshot;
                try
                {
                    rawSnapshot = RawGameController.RawGameControllers.ToList();
                }
                catch
                {
                    rawSnapshot = [];
                }

                var currentConnectedHwIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var c in _config.Controllers)
                {
                    c.IsConnected = false;
                }

                // Ensure built-in handheld sensor entry always exists as primary fallback if no external pads are found
                int playerIndex = 0;

                foreach (RawGameController raw in rawSnapshot)
                {
                    ushort vid;
                    ushort pid;
                    string? rawDisplayName;
                    try
                    {
                        vid = raw.HardwareVendorId;
                        pid = raw.HardwareProductId;
                        rawDisplayName = raw.DisplayName;
                    }
                    catch
                    {
                        continue;
                    }

                    string hwId = $"VID_{vid:X4}&PID_{pid:X4}";
                    // Distinguish multiple identical controllers by index if needed
                    string uniqueKey = hwId;
                    int suffix = 2;
                    while (!currentConnectedHwIds.Add(uniqueKey))
                    {
                        uniqueKey = $"{hwId}#{suffix++}";
                    }

                    var known = _config.Controllers.FirstOrDefault(c =>
                        string.Equals(c.HardwareId, uniqueKey, StringComparison.OrdinalIgnoreCase));

                    string friendlyName = ResolveFriendlyControllerName(vid, pid, rawDisplayName);

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
                        pendingNotifications.Add((
                            loc.Format("ToastControllerConnectedTitleFormat", known.DisplayName),
                            loc.Format("ToastControllerConnectedSubFormat", playerIndex + 1, known.AssignedUdpPort, known.AssignedDsuSlot, known.AssignedProfileName),
                            profileToApply,
                            rawSnapshot.Count + _simulatedControllers.Count
                        ));
                    }

                    playerIndex++;
                }

                // Append Developer Options simulated controllers
                foreach (var sim in _simulatedControllers)
                {
                    currentConnectedHwIds.Add(sim.HwId);
                    var known = _config.Controllers.FirstOrDefault(c =>
                        string.Equals(c.HardwareId, sim.HwId, StringComparison.OrdinalIgnoreCase));

                    if (known is null)
                    {
                        known = new KnownControllerConfig
                        {
                            HardwareId = sim.HwId,
                            DisplayName = sim.DisplayName,
                            AssignedProfileName = sim.DefaultProfile,
                            AutoSwitchProfile = true,
                            LastSeenUtc = DateTime.UtcNow
                        };
                        _config.Controllers.Add(known);
                    }

                    known.IsConnected = true;
                    known.LastSeenUtc = DateTime.UtcNow;
                    known.AssignedPlayerIndex = playerIndex;
                    known.AssignedUdpPort = playerIndex < 4 ? basePort : secondaryPort;
                    known.AssignedDsuSlot = playerIndex % 4;

                    if (!_isInitialScan && !_previousConnectedHwIds.Contains(sim.HwId))
                    {
                        string? profileToApply = (_config.AutoSwitchByController && known.AutoSwitchProfile)
                            ? known.AssignedProfileName
                            : null;

                        var loc = LocalizationService.Instance;
                        pendingNotifications.Add((
                            loc.Format("ToastControllerConnectedTitleFormat", known.DisplayName),
                            loc.Format("ToastControllerConnectedSubFormat", playerIndex + 1, known.AssignedUdpPort, known.AssignedDsuSlot, known.AssignedProfileName),
                            profileToApply,
                            rawSnapshot.Count + _simulatedControllers.Count
                        ));
                    }

                    playerIndex++;
                }

                const string builtInId = "BUILTIN_HANDHELD_IMU";
                if (rawSnapshot.Count == 0 && _simulatedControllers.Count == 0)
                {
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
                else
                {
                    if (_config.Controllers.RemoveAll(c => string.Equals(c.HardwareId, builtInId, StringComparison.OrdinalIgnoreCase)) > 0)
                    {
                        changed = true;
                    }
                }

                if (!_previousConnectedHwIds.SetEquals(currentConnectedHwIds))
                {
                    changed = true;
                }
                _previousConnectedHwIds = currentConnectedHwIds;

                if (changed)
                {
                    _config.Save();
                }

                orderedSnapshot = _config.Controllers
                    .OrderByDescending(c => c.IsConnected)
                    .ThenBy(c => c.AssignedPlayerIndex)
                    .ThenByDescending(c => c.LastSeenUtc)
                    .ToList();
            }
            finally
            {
                _isScanning = false;
            }
        }

        if (changed)
        {
            ControllersChanged?.Invoke(this, EventArgs.Empty);
        }

        foreach (var notification in pendingNotifications)
        {
            AutomationNotification?.Invoke(this, notification);
        }

        return orderedSnapshot;
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

            _lastDetectedEmulatorProcess = procName;

            if (procName.Contains("dolphin", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(() => EnsureDolphinDsuConfigured());
            }

            if (!_config.AutoSwitchByEmulator)
                return;

            var matchedRule = _config.EmulatorRules.FirstOrDefault(r =>
                r.IsEnabled && procName.Contains(r.ProcessName, StringComparison.OrdinalIgnoreCase));

            if (matchedRule is not null)
            {
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

    /// <summary>
    /// Automatically ensures Dolphin Emulator's DSUClient.ini has a non-empty description
    /// (preventing Dolphin's DeviceQualifier::FromString empty-name EOF bug) and prefixes
    /// unprefixed IMUAccelerometer / IMUGyroscope bindings in WiimoteNew.ini with DSUClient/0/&lt;description&gt;:
    /// when an XInput/WGInput/SDL gamepad is selected as the primary device.
    /// </summary>
    public static void EnsureDolphinDsuConfigured(int dsuPort = 26760)
    {
        try
        {
            var configDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            string[] candidates =
            [
                Path.Combine(appData, "Dolphin Emulator", "Config"),
                Path.Combine(appData, "emudeck", "Emulators", "Dolphin-x64", "User", "Config"),
                Path.Combine(docs, "Dolphin Emulator", "Config")
            ];

            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate))
                    configDirs.Add(candidate);
            }

            try
            {
                foreach (Process p in Process.GetProcessesByName("Dolphin"))
                {
                    using (p)
                    {
                        string? exeDir = Path.GetDirectoryName(p.MainModule?.FileName);
                        if (!string.IsNullOrEmpty(exeDir))
                        {
                            string portableCfg = Path.Combine(exeDir, "User", "Config");
                            if (Directory.Exists(portableCfg))
                                configDirs.Add(portableCfg);
                        }
                    }
                }
            }
            catch
            {
                // Ignore process enumeration access errors
            }

            foreach (string configDir in configDirs)
            {
                string dsuIniPath = Path.Combine(configDir, "DSUClient.ini");
                string dsuDescription = "HandheldGyroDSU";

                if (File.Exists(dsuIniPath))
                {
                    string dsuText = File.ReadAllText(dsuIniPath);
                    bool dsuChanged = false;

                    // Fix empty description ":127.0.0.1:26760" which triggers Dolphin's DeviceQualifier::FromString bug
                    if (Regex.IsMatch(dsuText, @"(?m)^Entries\s*=\s*:127\.0\.0\.1:"))
                    {
                        dsuText = Regex.Replace(
                            dsuText,
                            @"(?m)^(Entries\s*=\s*):127\.0\.0\.1:",
                            $"${{1}}{dsuDescription}:127.0.0.1:");
                        dsuChanged = true;
                    }

                    Match descMatch = Regex.Match(dsuText, @"(?m)^Entries\s*=\s*([^:;\r\n]+):127\.0\.0\.1:");
                    if (descMatch.Success && !string.IsNullOrWhiteSpace(descMatch.Groups[1].Value))
                    {
                        dsuDescription = descMatch.Groups[1].Value.Trim();
                    }

                    if (dsuChanged)
                    {
                        File.WriteAllText(dsuIniPath, dsuText);
                    }
                }
                else
                {
                    File.WriteAllText(
                        dsuIniPath,
                        $"[General]{Environment.NewLine}Enabled = True{Environment.NewLine}Entries = {dsuDescription}:127.0.0.1:{dsuPort};{Environment.NewLine}");
                }

                string prefix = $"DSUClient/0/{dsuDescription}:";
                EnsureDolphinControllerProfilesExist(configDir, prefix);

                string wiimoteIniPath = Path.Combine(configDir, "WiimoteNew.ini");
                if (File.Exists(wiimoteIniPath))
                {
                    string wiimoteText = File.ReadAllText(wiimoteIniPath);

                    // Replace unprefixed `Accel ...` or `Gyro ...` bindings with `DSUClient/0/<description>:Accel ...`
                    string updatedWiimote = Regex.Replace(
                        wiimoteText,
                        @"(?m)^(\s*IMU(?:Accelerometer|Gyroscope)/[^=\r\n]+=\s*)`((?:Accel|Gyro)\s+[^`]+)`",
                        $"${{1}}`{prefix}${{2}}`");

                    // Ensure IRPassthrough is disabled and IMUIR (gyro pointer) is enabled
                    updatedWiimote = Regex.Replace(updatedWiimote, @"(?m)^(\s*IRPassthrough/Enabled\s*=\s*)True", "${1}False");
                    updatedWiimote = Regex.Replace(updatedWiimote, @"(?m)^(\s*IMUIR/Enabled\s*=\s*)False", "${1}True");

                    // If Wiimote1 was bound to Keyboard/Mouse clicks (`Click 0`), upgrade WiimoteNew.ini to the Xbox + DSU profile
                    if (updatedWiimote.Contains("Buttons/A = `Click 0`", StringComparison.OrdinalIgnoreCase))
                    {
                        updatedWiimote = BuildDolphinWiimoteNewIni(prefix);
                    }

                    if (!string.Equals(wiimoteText, updatedWiimote, StringComparison.Ordinal))
                    {
                        File.WriteAllText(wiimoteIniPath, updatedWiimote);
                    }
                }
                else
                {
                    File.WriteAllText(wiimoteIniPath, BuildDolphinWiimoteNewIni(prefix));
                }
            }
        }
        catch
        {
            // Ignore file I/O contention if emulator is actively writing INI files
        }
    }

    private static void EnsureDolphinControllerProfilesExist(string configDir, string dsuPrefix)
    {
        string wiimoteProfilesDir = Path.Combine(configDir, "Profiles", "Wiimote");
        Directory.CreateDirectory(wiimoteProfilesDir);

        string xboxWiimoteProfilePath = Path.Combine(wiimoteProfilesDir, "HandheldGyroDSU_Xbox.ini");
        File.WriteAllText(xboxWiimoteProfilePath, BuildDolphinWiimoteProfileSection("Profile", dsuPrefix, sideways: false));

        string sidewaysWiimoteProfilePath = Path.Combine(wiimoteProfilesDir, "HandheldGyroDSU_Sideways.ini");
        File.WriteAllText(sidewaysWiimoteProfilePath, BuildDolphinWiimoteProfileSection("Profile", dsuPrefix, sideways: true));

        string gcProfilesDir = Path.Combine(configDir, "Profiles", "GCPad");
        Directory.CreateDirectory(gcProfilesDir);

        string xboxGcProfilePath = Path.Combine(gcProfilesDir, "HandheldGyroDSU_Xbox.ini");
        File.WriteAllText(xboxGcProfilePath, BuildDolphinGcPadProfileSection("Profile"));
    }

    private static string BuildDolphinWiimoteNewIni(string dsuPrefix)
    {
        return BuildDolphinWiimoteProfileSection("Wiimote1", dsuPrefix, sideways: false) +
               Environment.NewLine +
               "[Wiimote2]" + Environment.NewLine +
               "Source = 0" + Environment.NewLine +
               "[Wiimote3]" + Environment.NewLine +
               "Source = 0" + Environment.NewLine +
               "[Wiimote4]" + Environment.NewLine +
               "Source = 0" + Environment.NewLine +
               "[BalanceBoard]" + Environment.NewLine +
               "Source = 0" + Environment.NewLine;
    }

    private static string BuildDolphinWiimoteProfileSection(string sectionName, string dsuPrefix, bool sideways)
    {
        string extensionLine = sideways ? "Extension = None" : "Extension = Nunchuk";
        string sidewaysLine = sideways ? "Options/Sideways Wiimote = True" : "Options/Sideways Wiimote = False";

        return $"""
            [{sectionName}]
            Device = XInput/0/Gamepad
            Buttons/A = `Button A`
            Buttons/B = `Button B`
            Buttons/1 = `Button X`
            Buttons/2 = `Button Y`
            Buttons/- = Back
            Buttons/+ = Start
            Buttons/Home = Guide
            D-Pad/Up = `Pad N`
            D-Pad/Down = `Pad S`
            D-Pad/Left = `Pad W`
            D-Pad/Right = `Pad E`
            IR/Vertical Offset = 12.
            IR/Total Yaw = 19.
            IR/Total Pitch = 22.
            IR/Auto-Hide = False
            IR/Up = `Right Y+`
            IR/Down = `Right Y-`
            IR/Left = `Right X-`
            IR/Right = `Right X+`
            Shake/X = `Shoulder L`
            Shake/Y = `Shoulder L`
            Shake/Z = `Shoulder L`
            IMUAccelerometer/Up = `{dsuPrefix}Accel Up`
            IMUAccelerometer/Down = `{dsuPrefix}Accel Down`
            IMUAccelerometer/Left = `{dsuPrefix}Accel Left`
            IMUAccelerometer/Right = `{dsuPrefix}Accel Right`
            IMUAccelerometer/Forward = `{dsuPrefix}Accel Forward`
            IMUAccelerometer/Backward = `{dsuPrefix}Accel Backward`
            IMUGyroscope/Pitch Up = `{dsuPrefix}Gyro Pitch Up`
            IMUGyroscope/Pitch Down = `{dsuPrefix}Gyro Pitch Down`
            IMUGyroscope/Roll Left = `{dsuPrefix}Gyro Roll Left`
            IMUGyroscope/Roll Right = `{dsuPrefix}Gyro Roll Right`
            IMUGyroscope/Yaw Left = `{dsuPrefix}Gyro Yaw Left`
            IMUGyroscope/Yaw Right = `{dsuPrefix}Gyro Yaw Right`
            IMUIR/Enabled = True
            IMUIR/Recenter = `Thumb R`
            IRPassthrough/Enabled = False
            {extensionLine}
            {sidewaysLine}
            Nunchuk/Buttons/C = `Shoulder R`
            Nunchuk/Buttons/Z = `Trigger R`
            Nunchuk/Stick/Up = `Left Y+`
            Nunchuk/Stick/Down = `Left Y-`
            Nunchuk/Stick/Left = `Left X-`
            Nunchuk/Stick/Right = `Left X+`
            Nunchuk/Shake/X = `Trigger L`
            Nunchuk/Shake/Y = `Trigger L`
            Nunchuk/Shake/Z = `Trigger L`
            Classic/Buttons/A = `Button B`
            Classic/Buttons/B = `Button A`
            Classic/Buttons/X = `Button Y`
            Classic/Buttons/Y = `Button X`
            Classic/Buttons/ZL = `Shoulder L`
            Classic/Buttons/ZR = `Shoulder R`
            Classic/Buttons/- = Back
            Classic/Buttons/+ = Start
            Classic/Buttons/Home = Guide
            Classic/Left Stick/Up = `Left Y+`
            Classic/Left Stick/Down = `Left Y-`
            Classic/Left Stick/Left = `Left X-`
            Classic/Left Stick/Right = `Left X+`
            Classic/Right Stick/Up = `Right Y+`
            Classic/Right Stick/Down = `Right Y-`
            Classic/Right Stick/Left = `Right X-`
            Classic/Right Stick/Right = `Right X+`
            Classic/Triggers/L = `Trigger L`
            Classic/Triggers/R = `Trigger R`
            Classic/Triggers/L-Analog = `Trigger L`
            Classic/Triggers/R-Analog = `Trigger R`
            Classic/D-Pad/Up = `Pad N`
            Classic/D-Pad/Down = `Pad S`
            Classic/D-Pad/Left = `Pad W`
            Classic/D-Pad/Right = `Pad E`
            Rumble/Motor = `Motor L`|`Motor R`
            Source = 1
            """;
    }

    private static string BuildDolphinGcPadProfileSection(string sectionName)
    {
        return $"""
            [{sectionName}]
            Device = XInput/0/Gamepad
            Buttons/A = `Button A`
            Buttons/B = `Button B`
            Buttons/X = `Button X`
            Buttons/Y = `Button Y`
            Buttons/Z = `Shoulder R`
            Buttons/Start = Start
            Main Stick/Up = `Left Y+`
            Main Stick/Down = `Left Y-`
            Main Stick/Left = `Left X-`
            Main Stick/Right = `Left X+`
            C-Stick/Up = `Right Y+`
            C-Stick/Down = `Right Y-`
            C-Stick/Left = `Right X-`
            C-Stick/Right = `Right X+`
            Triggers/L = `Trigger L`
            Triggers/R = `Trigger R`
            Triggers/L-Analog = `Trigger L`
            Triggers/R-Analog = `Trigger R`
            D-Pad/Up = `Pad N`
            D-Pad/Down = `Pad S`
            D-Pad/Left = `Pad W`
            D-Pad/Right = `Pad E`
            Rumble/Motor = `Motor L`|`Motor R`
            """;
    }

    public void Dispose()
    {
        _watcherTimer.Stop();
        RawGameController.RawGameControllerAdded -= OnRawControllerChanged;
        RawGameController.RawGameControllerRemoved -= OnRawControllerChanged;
    }
}
