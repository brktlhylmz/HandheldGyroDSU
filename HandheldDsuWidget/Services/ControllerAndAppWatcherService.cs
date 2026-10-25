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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint dwUserIndex, out XInputState pState);

    private readonly DispatcherTimer _watcherTimer;
    private readonly DispatcherTimer _hotkeyTimer;
    private readonly object _syncLock = new();
    private readonly List<(string HwId, string DisplayName, string DefaultProfile)> _simulatedControllers = [];
    private AutomationConfig _config;
    private string _lastDetectedEmulatorProcess = string.Empty;
    private string _baseProfileName = "Standard Landscape";
    private string? _activeRuleProcessName;
    private string? _activeRuleDisplayName;
    private HashSet<string> _previousConnectedHwIds = [];
    private bool _isInitialScan = true;
    private bool _isScanning;

    private string? _recordingHardwareId;
    private bool _recordingReadyAfterRelease;
    private ushort _recordedXInputMask;
    private DateTime _recordingStartedUtc;
    private bool _wasHotkeyDown;
    private DateTime _lastHotkeyTriggerUtc = DateTime.MinValue;

    public AutomationConfig Config => _config;
    public string? RecordingHardwareId => _recordingHardwareId;
    public int SimulatedControllerCount
    {
        get { lock (_syncLock) return _simulatedControllers.Count; }
    }

    public event EventHandler<(string Title, string Subtitle, string? ProfileToApply, int ConnectedCount)>? AutomationNotification;
    public event EventHandler<(string ControllerName, string HotkeyDisplay)>? RecenterHotkeyTriggered;
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

        _hotkeyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(35)
        };
        _hotkeyTimer.Tick += HotkeyTimer_Tick;

        RawGameController.RawGameControllerAdded += OnRawControllerChanged;
        RawGameController.RawGameControllerRemoved += OnRawControllerChanged;
    }

    public void Start()
    {
        _ = Task.Run(() => EnsureDolphinDsuConfigured());
        ScanControllers(basePort: 26760);
        _isInitialScan = false;
        _watcherTimer.Start();
        _hotkeyTimer.Start();
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

    public void RenameController(string hardwareId, string? customName)
    {
        lock (_syncLock)
        {
            var ctrl = _config.Controllers.FirstOrDefault(c =>
                string.Equals(c.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase));
            if (ctrl is not null)
            {
                ctrl.CustomName = (customName ?? string.Empty).Trim();
                _config.Save();
            }
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

        if (cleanProc.Contains("dolphin", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(() => EnsureDolphinDsuConfigured());
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
        bool inputActiveChanged = false;
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

                var prevActiveSet = new HashSet<string>(
                    _config.Controllers.Where(c => c.IsInputActive).Select(c => c.HardwareId),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var c in _config.Controllers)
                {
                    c.IsConnected = false;
                    c.IsInputActive = false;
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
                    int instanceNumber = suffix - 1;

                    var known = _config.Controllers.FirstOrDefault(c =>
                        string.Equals(c.HardwareId, uniqueKey, StringComparison.OrdinalIgnoreCase));

                    string friendlyName = ResolveFriendlyControllerName(vid, pid, rawDisplayName);
                    if (instanceNumber > 1 && !friendlyName.EndsWith($"#{instanceNumber}", StringComparison.Ordinal))
                    {
                        friendlyName = $"{friendlyName} #{instanceNumber}";
                    }

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
                    known.IsInputActive = IsRawControllerActive(raw);
                    if (known.IsInputActive != prevActiveSet.Contains(uniqueKey))
                    {
                        inputActiveChanged = true;
                    }
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
                            loc.Format("ToastControllerConnectedTitleFormat", known.EffectiveName),
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
                            loc.Format("ToastControllerConnectedTitleFormat", known.EffectiveName),
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

        if (changed || inputActiveChanged)
        {
            ControllersChanged?.Invoke(this, EventArgs.Empty);
        }

        foreach (var notification in pendingNotifications)
        {
            AutomationNotification?.Invoke(this, notification);
        }

        return orderedSnapshot;
    }

    public void SetBaseProfileName(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            return;

        if (_activeRuleProcessName is null)
        {
            _baseProfileName = profileName;
        }
    }

    public string ResolveFallbackControllerProfile()
    {
        lock (_syncLock)
        {
            var primaryConnected = _config.Controllers
                .Where(c => c.IsConnected && !string.IsNullOrWhiteSpace(c.AssignedProfileName))
                .OrderBy(c => c.AssignedPlayerIndex)
                .FirstOrDefault();

            if (primaryConnected is not null)
            {
                return primaryConnected.AssignedProfileName;
            }
        }

        return !string.IsNullOrWhiteSpace(_baseProfileName) ? _baseProfileName : "Standard Landscape";
    }

    public sealed record RunningAppEntry(string ProcessName, string DisplayName, string ComboLabel);

    private static readonly HashSet<string> ExcludedRunningProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "applicationframehost",
        "textinputhost",
        "searchhost",
        "startmenuexperiencehost",
        "shellexperiencehost",
        "systemsettings",
        "taskmgr",
        "handheldgyrodsu",
        "handhelddsuwidget",
        "handhelddsuserver",
        "svchost",
        "dwm",
        "csrss",
        "conhost",
        "powershell",
        "pwsh",
        "cmd",
        "steamwebhelper",
        "epicwebhelper",
        "crashhandler",
        "crashhandler64"
    };

    public IReadOnlyList<RunningAppEntry> GetRunningApplications()
    {
        var results = new List<RunningAppEntry>();
        var seenProc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero)
                            continue;

                        string procName = (p.ProcessName ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(procName) || ExcludedRunningProcessNames.Contains(procName))
                            continue;

                        string windowTitle = (p.MainWindowTitle ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(windowTitle))
                            continue;

                        string lowerProc = procName.ToLowerInvariant();
                        if (!seenProc.Add(lowerProc))
                            continue;

                        string friendly = AutomationConfig.ResolveFriendlyProcessDisplayName(lowerProc);
                        string displayName = !string.Equals(friendly, lowerProc, StringComparison.OrdinalIgnoreCase)
                            ? friendly
                            : (windowTitle.Length > 38 ? windowTitle[..38].Trim() + "…" : windowTitle);

                        results.Add(new RunningAppEntry(
                            lowerProc,
                            displayName,
                            $"{displayName} ({lowerProc}.exe)"));
                    }
                    catch
                    {
                        // Ignore inaccessible processes
                    }
                }
            }
        }
        catch
        {
            // Ignore process enumeration errors
        }

        return results.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsProcessStillRunning(string processNamePattern)
    {
        try
        {
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.ProcessName.Contains(processNamePattern, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // Ignore inaccessible processes
                    }
                }
            }
        }
        catch
        {
            // Ignore process enumeration errors
        }
        return false;
    }

    private void CheckActiveRuleProcessExit()
    {
        if (_activeRuleProcessName is null)
            return;

        bool stillRunning = _config.AutoSwitchByEmulator && IsProcessStillRunning(_activeRuleProcessName);
        if (!stillRunning)
        {
            string closedDisplay = _activeRuleDisplayName ?? _activeRuleProcessName;
            _activeRuleProcessName = null;
            _activeRuleDisplayName = null;
            _lastDetectedEmulatorProcess = string.Empty;

            string fallbackProfile = ResolveFallbackControllerProfile();
            int connectedCount = _config.Controllers.Count(c => c.IsConnected);
            var loc = LocalizationService.Instance;
            AutomationNotification?.Invoke(
                this,
                (
                    loc.Format("ToastEmulatorClosedTitleFormat", closedDisplay),
                    loc.Format("ToastEmulatorClosedSubFormat", fallbackProfile),
                    fallbackProfile,
                    connectedCount
                ));
        }
    }

    private void CheckForegroundEmulator()
    {
        CheckActiveRuleProcessExit();
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

            CheckActiveRuleProcessExit();
        if (!_config.AutoSwitchByEmulator)
                return;

            var matchedRule = _config.EmulatorRules.FirstOrDefault(r =>
                r.IsEnabled && procName.Contains(r.ProcessName, StringComparison.OrdinalIgnoreCase));

            if (matchedRule is not null)
            {
                _activeRuleProcessName = matchedRule.ProcessName;
                _activeRuleDisplayName = matchedRule.DisplayName;
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
                    updatedWiimote = Regex.Replace(updatedWiimote, @"(?m)^(\s*IR/Vertical Offset\s*=\s*)12\.", "${1}0.");
                    updatedWiimote = Regex.Replace(updatedWiimote, @"(?m)^(\s*IR/Total Yaw\s*=\s*)19\.", "${1}35.");
                    updatedWiimote = Regex.Replace(updatedWiimote, @"(?m)^(\s*IR/Total Pitch\s*=\s*)22\.", "${1}30.");
                    updatedWiimote = Regex.Replace(updatedWiimote, @"(?m)^(\s*Options/Upright Wiimote\s*=\s*)True", "${1}False");

                    // If Wiimote1 was bound to Keyboard/Mouse clicks (`Click 0`) or missing Extension in Sideways mode, upgrade WiimoteNew.ini
                    if (updatedWiimote.Contains("Buttons/A = `Click 0`", StringComparison.OrdinalIgnoreCase))
                    {
                        updatedWiimote = BuildDolphinWiimoteNewIni(prefix);
                    }
                    else if (updatedWiimote.Contains("Options/Sideways Wiimote = True", StringComparison.OrdinalIgnoreCase))
                    {
                        updatedWiimote = BuildDolphinWiimoteNewIni(prefix, sideways: true);
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

    private static string BuildDolphinWiimoteNewIni(string dsuPrefix, bool sideways = false)
    {
        return BuildDolphinWiimoteProfileSection("Wiimote1", dsuPrefix, sideways: sideways) +
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
        string sidewaysLine = sideways
            ? $"Options/Sideways Wiimote = True{Environment.NewLine}Options/Upright Wiimote = False{Environment.NewLine}Hotkeys/Upright Toggle = `Thumb L`"
            : $"Options/Sideways Wiimote = False{Environment.NewLine}Options/Upright Wiimote = False{Environment.NewLine}Hotkeys/Sideways Toggle = `Thumb L`{Environment.NewLine}Hotkeys/Upright Toggle = `Thumb L`";

        string btnA = sideways ? "`Button Y`" : "`Button A`";
        string btnB = sideways ? "`Button B`|`Trigger R`|`Shoulder R`" : "`Button B`";
        string btn1 = sideways ? "`Button X`|`Trigger L`" : "`Button X`";
        string btn2 = sideways ? "`Button A`|`Button Y`" : "`Button Y`";

        string accelLeft = sideways ? $"`{dsuPrefix}Accel Left` * 1.8" : $"`{dsuPrefix}Accel Left`";
        string accelRight = sideways ? $"`{dsuPrefix}Accel Right` * 1.8" : $"`{dsuPrefix}Accel Right`";

        return $"""
            [{sectionName}]
            Device = XInput/0/Gamepad
            Buttons/A = {btnA}
            Buttons/B = {btnB}
            Buttons/1 = {btn1}
            Buttons/2 = {btn2}
            Buttons/- = Back
            Buttons/+ = Start
            Buttons/Home = Guide
            D-Pad/Up = `Pad N`
            D-Pad/Down = `Pad S`
            D-Pad/Left = `Pad W`
            D-Pad/Right = `Pad E`
            IR/Vertical Offset = 0.
            IR/Total Yaw = 35.
            IR/Total Pitch = 30.
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
            IMUAccelerometer/Left = {accelLeft}
            IMUAccelerometer/Right = {accelRight}
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
        _hotkeyTimer.Stop();
        RawGameController.RawGameControllerAdded -= OnRawControllerChanged;
        RawGameController.RawGameControllerRemoved -= OnRawControllerChanged;
    }

    public void StartRecordingHotkey(string hardwareId)
    {
        _recordingHardwareId = hardwareId;
        _recordingReadyAfterRelease = false;
        _recordedXInputMask = 0;
        _recordingStartedUtc = DateTime.UtcNow;
        ControllersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CancelRecordingHotkey()
    {
        if (_recordingHardwareId is null)
            return;

        _recordingHardwareId = null;
        _recordedXInputMask = 0;
        ControllersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearControllerHotkey(string hardwareId)
    {
        lock (_syncLock)
        {
            var ctrl = _config.Controllers.FirstOrDefault(c =>
                string.Equals(c.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase));
            if (ctrl is not null)
            {
                ctrl.RecenterHotkey = string.Empty;
                ctrl.RecenterHotkeyDisplay = string.Empty;
                _config.Save();
            }
        }

        if (string.Equals(_recordingHardwareId, hardwareId, StringComparison.OrdinalIgnoreCase))
        {
            _recordingHardwareId = null;
        }

        ControllersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HotkeyTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (_recordingHardwareId is not null)
            {
                ProcessHotkeyRecordingTick();
                return;
            }

            ProcessHotkeyTriggerTick();
        }
        catch
        {
            // Ignore transient input polling errors
        }
    }

    private void ProcessHotkeyRecordingTick()
    {
        if ((DateTime.UtcNow - _recordingStartedUtc).TotalSeconds > 8.0)
        {
            CancelRecordingHotkey();
            return;
        }

        ushort currentXInput = GetMergedXInputButtons();
        int extraRawBtn = GetFirstPressedExtraRawButton();
        int pressedVKey = GetFirstPressedMacroKey();

        if (!_recordingReadyAfterRelease)
        {
            if (currentXInput == 0 && extraRawBtn < 0 && pressedVKey < 0)
            {
                _recordingReadyAfterRelease = true;
            }
            return;
        }

        if (extraRawBtn >= 0)
        {
            CompleteHotkeyRecording($"RawBtn:{extraRawBtn}", $"Paddle / Btn #{extraRawBtn + 1}");
            return;
        }

        if (pressedVKey >= 0)
        {
            CompleteHotkeyRecording($"Key:{pressedVKey:X2}", FormatVirtualKeyName(pressedVKey));
            return;
        }

        if (currentXInput != 0)
        {
            _recordedXInputMask |= currentXInput;
            return;
        }

        if (_recordedXInputMask != 0 && currentXInput == 0)
        {
            CompleteHotkeyRecording($"XInput:{_recordedXInputMask:X4}", FormatXInputMask(_recordedXInputMask));
        }
    }

    private void CompleteHotkeyRecording(string code, string display)
    {
        string? targetHwId = _recordingHardwareId;
        _recordingHardwareId = null;
        _recordedXInputMask = 0;

        if (string.IsNullOrWhiteSpace(targetHwId))
            return;

        lock (_syncLock)
        {
            var ctrl = _config.Controllers.FirstOrDefault(c =>
                string.Equals(c.HardwareId, targetHwId, StringComparison.OrdinalIgnoreCase));
            if (ctrl is not null)
            {
                ctrl.RecenterHotkey = code;
                ctrl.RecenterHotkeyDisplay = display;
                _config.Save();
            }
        }

        _wasHotkeyDown = true;
        _lastHotkeyTriggerUtc = DateTime.UtcNow;
        ControllersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ProcessHotkeyTriggerTick()
    {
        List<KnownControllerConfig> activeWithHotkeys;
        lock (_syncLock)
        {
            activeWithHotkeys = _config.Controllers
                .Where(c => c.IsConnected && !string.IsNullOrWhiteSpace(c.RecenterHotkey))
                .ToList();
        }

        if (activeWithHotkeys.Count == 0)
        {
            _wasHotkeyDown = false;
            return;
        }

        ushort currentXInput = GetMergedXInputButtons();
        bool anyMatched = false;
        KnownControllerConfig? matchedController = null;

        foreach (KnownControllerConfig ctrl in activeWithHotkeys)
        {
            string hk = ctrl.RecenterHotkey;
            if (hk.StartsWith("XInput:", StringComparison.OrdinalIgnoreCase))
            {
                if (ushort.TryParse(hk.AsSpan(7), System.Globalization.NumberStyles.HexNumber, null, out ushort mask) &&
                    mask != 0 &&
                    (currentXInput & mask) == mask)
                {
                    anyMatched = true;
                    matchedController = ctrl;
                    break;
                }
            }
            else if (hk.StartsWith("RawBtn:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(hk.AsSpan(7), out int btnIndex) && IsExtraRawButtonPressed(btnIndex))
                {
                    anyMatched = true;
                    matchedController = ctrl;
                    break;
                }
            }
            else if (hk.StartsWith("Key:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(hk.AsSpan(4), System.Globalization.NumberStyles.HexNumber, null, out int vk) &&
                    (GetAsyncKeyState(vk) & 0x8000) != 0)
                {
                    anyMatched = true;
                    matchedController = ctrl;
                    break;
                }
            }
        }

        if (anyMatched && !_wasHotkeyDown && matchedController is not null)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastHotkeyTriggerUtc).TotalMilliseconds > 500)
            {
                _lastHotkeyTriggerUtc = now;
                RecenterHotkeyTriggered?.Invoke(
                    this,
                    (matchedController.EffectiveName, matchedController.RecenterHotkeyDisplay));
            }
        }

        _wasHotkeyDown = anyMatched;
    }

    private static bool IsRawControllerActive(RawGameController raw)
    {
        try
        {
            int btnCount = raw.ButtonCount;
            int swCount = raw.SwitchCount;
            int axCount = raw.AxisCount;

            bool[] buttons = btnCount > 0 ? new bool[btnCount] : [];
            GameControllerSwitchPosition[] switches = swCount > 0 ? new GameControllerSwitchPosition[swCount] : [];
            double[] axes = axCount > 0 ? new double[axCount] : [];

            raw.GetCurrentReading(buttons, switches, axes);

            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i])
                    return true;
            }

            for (int i = 0; i < switches.Length; i++)
            {
                if (switches[i] != GameControllerSwitchPosition.Center)
                    return true;
            }
        }
        catch
        {
            // Ignore transient reading errors
        }
        return false;
    }

    private static ushort GetMergedXInputButtons()
    {
        ushort merged = 0;
        for (uint i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetState(i, out XInputState state) == 0)
                {
                    merged |= state.Gamepad.wButtons;
                }
            }
            catch
            {
                break;
            }
        }
        return merged;
    }

    private static int GetFirstPressedExtraRawButton()
    {
        try
        {
            foreach (RawGameController raw in RawGameController.RawGameControllers)
            {
                int count = raw.ButtonCount;
                if (count <= 14)
                    continue;

                bool[] buttons = new bool[count];
                GameControllerSwitchPosition[] switches = new GameControllerSwitchPosition[raw.SwitchCount];
                double[] axes = new double[raw.AxisCount];
                raw.GetCurrentReading(buttons, switches, axes);

                for (int i = 14; i < count; i++)
                {
                    if (buttons[i])
                        return i;
                }
            }
        }
        catch
        {
            // Ignore RawGameController reading errors
        }
        return -1;
    }

    private static bool IsExtraRawButtonPressed(int targetIndex)
    {
        try
        {
            foreach (RawGameController raw in RawGameController.RawGameControllers)
            {
                int count = raw.ButtonCount;
                if (targetIndex < 0 || targetIndex >= count)
                    continue;

                bool[] buttons = new bool[count];
                GameControllerSwitchPosition[] switches = new GameControllerSwitchPosition[raw.SwitchCount];
                double[] axes = new double[raw.AxisCount];
                raw.GetCurrentReading(buttons, switches, axes);

                if (buttons[targetIndex])
                    return true;
            }
        }
        catch
        {
            // Ignore RawGameController reading errors
        }
        return false;
    }

    private static int GetFirstPressedMacroKey()
    {
        // Check F6..F24 (0x75..0x87), Insert (0x2D), Delete (0x2E), Home (0x24), End (0x23), PageUp/Down (0x21/0x22), Pause (0x13), ScrollLock (0x91)
        int[] candidateKeys =
        [
            0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B,
            0x7C, 0x7D, 0x7E, 0x7F, 0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x2D, 0x24, 0x23, 0x21, 0x22, 0x13, 0x91
        ];

        foreach (int vk in candidateKeys)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                return vk;
        }
        return -1;
    }

    private static string FormatVirtualKeyName(int vk)
    {
        if (vk is >= 0x70 and <= 0x87)
            return $"F{vk - 0x70 + 1}";

        return vk switch
        {
            0x2D => "Insert",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x13 => "Pause",
            0x91 => "ScrollLock",
            _ => $"Key 0x{vk:X2}"
        };
    }

    private static string FormatXInputMask(ushort mask)
    {
        var parts = new List<string>();
        if ((mask & 0x0020) != 0) parts.Add("Back");
        if ((mask & 0x0010) != 0) parts.Add("Start");
        if ((mask & 0x0100) != 0) parts.Add("LB");
        if ((mask & 0x0200) != 0) parts.Add("RB");
        if ((mask & 0x0040) != 0) parts.Add("L3");
        if ((mask & 0x0080) != 0) parts.Add("R3");
        if ((mask & 0x1000) != 0) parts.Add("A");
        if ((mask & 0x2000) != 0) parts.Add("B");
        if ((mask & 0x4000) != 0) parts.Add("X");
        if ((mask & 0x8000) != 0) parts.Add("Y");
        if ((mask & 0x0001) != 0) parts.Add("D-Up");
        if ((mask & 0x0002) != 0) parts.Add("D-Down");
        if ((mask & 0x0004) != 0) parts.Add("D-Left");
        if ((mask & 0x0008) != 0) parts.Add("D-Right");

        return parts.Count > 0 ? string.Join(" + ", parts) : $"0x{mask:X4}";
    }
}
