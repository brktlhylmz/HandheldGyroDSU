using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using HandheldDsuServer.Models;

namespace HandheldDsuWidget.Services;

/// <summary>
/// Activation trigger mode for experimental Gyro-to-Mouse aiming.
/// </summary>
public enum GyroMouseActivationMode
{
    HoldLeftTrigger = 0,
    HoldLeftShoulder = 1,
    AlwaysOn = 2
}

/// <summary>
/// High-frequency (250 Hz) driverless Gyro-to-Mouse translation service for PC and Steam games.
/// Converts calibrated IMU angular velocity into sub-pixel accumulated relative Win32 mouse deltas
/// without installing virtual controller drivers or modifying Steam files.
/// </summary>
public sealed class GyroMouseService : IDisposable
{
    private const int ERROR_SUCCESS = 0;
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const ushort XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100;
    private const byte TriggerThreshold = 45;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
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
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);

    private readonly DsuIpcClient _ipcClient;
    private readonly Func<bool> _isAppWindowFocused;
    private Thread? _workerThread;
    private volatile bool _stopRequested;
    private volatile bool _isEnabled;
    private volatile GyroMouseActivationMode _activationMode = GyroMouseActivationMode.HoldLeftTrigger;
    private float _speedMultiplier = 1.2f;
    private double _remainderX;
    private double _remainderY;

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

    public GyroMouseActivationMode ActivationMode
    {
        get => _activationMode;
        set => _activationMode = value;
    }

    public double SpeedMultiplier
    {
        get => Volatile.Read(ref _speedMultiplier);
        set => Volatile.Write(ref _speedMultiplier, (float)Math.Clamp(value, 0.2, 4.0));
    }

    public GyroMouseService(DsuIpcClient ipcClient, Func<bool> isAppWindowFocused)
    {
        _ipcClient = ipcClient;
        _isAppWindowFocused = isAppWindowFocused;
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
                SendRelativeMouseMove(moveX, moveY);
            }
        }
    }

    private bool ShouldTranslateMotionNow()
    {
        GyroMouseActivationMode mode = _activationMode;

        if (mode == GyroMouseActivationMode.AlwaysOn)
        {
            // Safety guard: pause Always-On mouse movement while the user is actively interacting
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

        for (uint i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetState(i, out XINPUT_STATE state) == ERROR_SUCCESS)
                {
                    if (mode == GyroMouseActivationMode.HoldLeftTrigger &&
                        state.Gamepad.bLeftTrigger >= TriggerThreshold)
                    {
                        return true;
                    }

                    if (mode == GyroMouseActivationMode.HoldLeftShoulder &&
                        (state.Gamepad.wButtons & XINPUT_GAMEPAD_LEFT_SHOULDER) != 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                break;
            }
        }

        return false;
    }

    private static void SendRelativeMouseMove(int dx, int dy)
    {
        INPUT input = new()
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                mouseData = 0,
                dwFlags = MOUSEEVENTF_MOVE,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        };

        _ = SendInput(1, ref input, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        _stopRequested = true;
    }
}
