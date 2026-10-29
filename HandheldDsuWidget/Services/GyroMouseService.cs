using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HandheldDsuServer.Models;
using HandheldDsuServer.Services.Platform;

namespace HandheldDsuWidget.Services;

/// <summary>
/// High-frequency (250 Hz) driverless Gyro-to-Mouse translation service for PC and Steam games.
/// Converts calibrated IMU angular velocity into sub-pixel accumulated relative mouse deltas
/// via <see cref="IMouseInjectionBackend"/> without installing virtual controller drivers.
/// Supports Always-Active mode or holding any user-assigned gamepad button/trigger via <see cref="IGamepadButtonBackend"/>.
/// </summary>
public sealed class GyroMouseService : IDisposable
{
    public const uint MaskLeftTrigger = 0x10000;
    public const uint MaskRightTrigger = 0x20000;

    private static readonly (uint Bit, string Label)[] ButtonMap =
    [
        (MaskLeftTrigger, "LT"),
        (MaskRightTrigger, "RT"),
        (0x0100, "LB"),
        (0x0200, "RB"),
        (0x1000, "A"),
        (0x2000, "B"),
        (0x4000, "X"),
        (0x8000, "Y"),
        (0x0040, "LS"),
        (0x0080, "RS"),
        (0x0020, "Back"),
        (0x0010, "Start"),
        (0x0001, "D-Pad Up"),
        (0x0002, "D-Pad Down"),
        (0x0004, "D-Pad Left"),
        (0x0008, "D-Pad Right")
    ];

    private readonly DsuIpcClient _ipcClient;
    private readonly Func<bool> _isAppWindowFocused;
    private readonly IMouseInjectionBackend _mouseBackend;
    private readonly IGamepadButtonBackend _gamepadBackend;
    private Thread? _workerThread;
    private volatile bool _stopRequested;
    private volatile bool _isEnabled;
    private volatile bool _alwaysActive;
    private uint _triggerMask = MaskLeftTrigger;
    private string _triggerDisplay = "LT";
    private float _speedMultiplier = 1.2f;
    private double _remainderX;
    private double _remainderY;

    private volatile bool _isListeningForTrigger;
    private volatile bool _listenWaitRelease;
    private uint _listenCandidateMask;
    private long _listenPressStartTicks;

    public event EventHandler<(uint Mask, string Display)>? TriggerButtonBound;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            if (!value)
            {
                _remainderX = 0;
                _remainderY = 0;
            }
        }
    }

    public bool AlwaysActive
    {
        get => _alwaysActive;
        set => _alwaysActive = value;
    }

    public uint TriggerMask
    {
        get => Volatile.Read(ref _triggerMask);
        set => Volatile.Write(ref _triggerMask, value == 0 ? MaskLeftTrigger : value);
    }

    public string TriggerDisplay
    {
        get => _triggerDisplay;
        set => _triggerDisplay = string.IsNullOrWhiteSpace(value) ? "LT" : value;
    }

    public double SpeedMultiplier
    {
        get => Volatile.Read(ref _speedMultiplier);
        set => Volatile.Write(ref _speedMultiplier, (float)Math.Clamp(value, 0.2, 4.0));
    }

    public bool IsListeningForTrigger => _isListeningForTrigger;

    public GyroMouseService(
        DsuIpcClient ipcClient,
        Func<bool> isAppWindowFocused,
        IMouseInjectionBackend? mouseBackend = null,
        IGamepadButtonBackend? gamepadBackend = null)
    {
        _ipcClient = ipcClient;
        _isAppWindowFocused = isAppWindowFocused;
        _mouseBackend = mouseBackend ?? PlatformInputBackends.Mouse;
        _gamepadBackend = gamepadBackend ?? PlatformInputBackends.Gamepad;
    }

    public void StartListeningForTrigger()
    {
        _listenCandidateMask = 0;
        _listenPressStartTicks = 0;
        _listenWaitRelease = true;
        _isListeningForTrigger = true;
    }

    public void CancelListeningForTrigger()
    {
        _isListeningForTrigger = false;
        _listenCandidateMask = 0;
    }

    public void Start()
    {
        if (_workerThread is not null)
            return;

        _stopRequested = false;
        _workerThread = new Thread(WorkerLoop)
        {
            Name = "GyroMouse-250Hz-Thread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _workerThread.Start();
    }

    private void WorkerLoop()
    {
        long prevTicks = Stopwatch.GetTimestamp();

        while (!_stopRequested)
        {
            Thread.Sleep(4);
            if (_stopRequested)
                break;

            long nowTicks = Stopwatch.GetTimestamp();
            double dtSeconds = Math.Clamp((nowTicks - prevTicks) / (double)Stopwatch.Frequency, 0.001, 0.050);
            prevTicks = nowTicks;

            if (_isListeningForTrigger)
            {
                PollTriggerBindingCapture(nowTicks);
                continue;
            }

            if (!_isEnabled)
                continue;

            if (!ShouldTranslateMotionNow())
            {
                _remainderX = 0;
                _remainderY = 0;
                continue;
            }

            if (!_ipcClient.TryGetLiveMotionSample(out MotionData motion))
                continue;

            // Player-space horizontal fusion (Yaw + Roll) so turning left/right works naturally
            // at any handheld viewing angle (upright or reclined), plus Pitch for vertical aim.
            double horizDegPerSec = (-motion.GyroYaw) + (motion.GyroRoll * 0.45);
            double vertDegPerSec = -motion.GyroPitch;

            if (Math.Abs(horizDegPerSec) < 0.08 && Math.Abs(vertDegPerSec) < 0.08)
                continue;

            // Base scale: ~18 pixels per degree of handheld rotation at 1.0x speed
            double scale = 18.0 * Volatile.Read(ref _speedMultiplier);
            double deltaX = (horizDegPerSec * dtSeconds * scale) + _remainderX;
            double deltaY = (vertDegPerSec * dtSeconds * scale) + _remainderY;

            int moveX = (int)Math.Truncate(deltaX);
            int moveY = (int)Math.Truncate(deltaY);

            _remainderX = deltaX - moveX;
            _remainderY = deltaY - moveY;

            if (moveX != 0 || moveY != 0)
            {
                _mouseBackend.SendRelativeMouseMove(moveX, moveY);
            }
        }
    }

    private void PollTriggerBindingCapture(long nowTicks)
    {
        uint activeMask = _gamepadBackend.ReadAnyControllerMask();

        if (_listenWaitRelease)
        {
            if (activeMask == 0)
            {
                _listenWaitRelease = false;
                _listenCandidateMask = 0;
                _listenPressStartTicks = 0;
            }
            return;
        }

        if (activeMask != 0)
        {
            if (_listenCandidateMask == 0)
            {
                _listenPressStartTicks = nowTicks;
            }
            _listenCandidateMask |= activeMask;

            double heldMs = ((nowTicks - _listenPressStartTicks) * 1000.0) / Stopwatch.Frequency;
            if (heldMs >= 180.0)
            {
                FinalizeCapturedTrigger(_listenCandidateMask);
            }
        }
        else if (_listenCandidateMask != 0)
        {
            FinalizeCapturedTrigger(_listenCandidateMask);
        }
    }

    private void FinalizeCapturedTrigger(uint capturedMask)
    {
        _isListeningForTrigger = false;
        _listenCandidateMask = 0;

        string display = FormatTriggerMask(capturedMask);
        Volatile.Write(ref _triggerMask, capturedMask);
        _triggerDisplay = display;

        TriggerButtonBound?.Invoke(this, (capturedMask, display));
    }

    private bool ShouldTranslateMotionNow()
    {
        if (_alwaysActive)
        {
            // Safety guard: pause Always-Active mouse movement while the user is actively interacting
            // with the HandheldGyroDSU window itself so the cursor never runs away in settings.
            try
            {
                if (_isAppWindowFocused())
                    return false;
            }
            catch
            {
                // Ignore thread state exceptions during shutdown
            }
            return true;
        }

        uint requiredMask = Volatile.Read(ref _triggerMask);
        if (requiredMask == 0)
            requiredMask = MaskLeftTrigger;

        return _gamepadBackend.IsMaskPressed(requiredMask);
    }

    public static string FormatTriggerMask(uint mask)
    {
        if (mask == 0)
            return "LT";

        var names = new List<string>(2);
        foreach (var (bit, label) in ButtonMap)
        {
            if ((mask & bit) != 0)
            {
                names.Add(label);
            }
        }

        return names.Count > 0 ? string.Join(" + ", names) : "LT";
    }

    public void Dispose()
    {
        _stopRequested = true;
    }
}
