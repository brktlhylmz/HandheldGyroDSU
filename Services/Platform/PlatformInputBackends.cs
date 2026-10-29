using System;
using System.Runtime.InteropServices;

namespace HandheldDsuServer.Services.Platform;

/// <summary>
/// Provides runtime-selected OS backends for relative mouse injection and gamepad trigger polling.
/// </summary>
public static class PlatformInputBackends
{
    public static IMouseInjectionBackend Mouse { get; } = CreateMouseBackend();
    public static IGamepadButtonBackend Gamepad { get; } = CreateGamepadBackend();

    private static IMouseInjectionBackend CreateMouseBackend()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsSendInputMouseBackend();
        if (OperatingSystem.IsMacOS())
            return new MacOsCoreGraphicsMouseBackend();
        return new LinuxUinputMouseBackend();
    }

    private static IGamepadButtonBackend CreateGamepadBackend()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsXInputGamepadBackend();
        return new NullGamepadButtonBackend();
    }
}

/// <summary>
/// Windows implementation of <see cref="IMouseInjectionBackend"/> using Win32 SendInput (user32.dll).
/// </summary>
public sealed class WindowsSendInputMouseBackend : IMouseInjectionBackend
{
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);

    public void SendRelativeMouseMove(int dx, int dy)
    {
        if (!OperatingSystem.IsWindows())
            return;

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
}

/// <summary>
/// macOS implementation of <see cref="IMouseInjectionBackend"/> targeting CoreGraphics (ApplicationServices).
/// See Issue #7 for full macOS bundle integration.
/// </summary>
public sealed class MacOsCoreGraphicsMouseBackend : IMouseInjectionBackend
{
    public void SendRelativeMouseMove(int dx, int dy)
    {
        // Tracked under Issue #7: CoreGraphics CGEventCreateMouseEvent / CGEventPost integration.
    }
}

/// <summary>
/// Linux implementation of <see cref="IMouseInjectionBackend"/> targeting /dev/uinput EV_REL events.
/// See Issue #8 for full Linux uinput daemon integration.
/// </summary>
public sealed class LinuxUinputMouseBackend : IMouseInjectionBackend
{
    public void SendRelativeMouseMove(int dx, int dy)
    {
        // Tracked under Issue #8: /dev/uinput EV_REL (REL_X / REL_Y) relative cursor injection.
    }
}

/// <summary>
/// Windows XInput implementation of <see cref="IGamepadButtonBackend"/> (xinput1_4.dll).
/// </summary>
public sealed class WindowsXInputGamepadBackend : IGamepadButtonBackend
{
    public const uint MaskLeftTrigger = 0x10000;
    public const uint MaskRightTrigger = 0x20000;
    private const int ERROR_SUCCESS = 0;
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

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    public uint ReadAnyControllerMask()
    {
        if (!OperatingSystem.IsWindows())
            return 0;

        for (uint i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetState(i, out XINPUT_STATE state) == ERROR_SUCCESS)
                {
                    uint mask = ReadCombinedMask(in state.Gamepad);
                    if (mask != 0)
                        return mask;
                }
            }
            catch
            {
                break;
            }
        }
        return 0;
    }

    public bool IsMaskPressed(uint requiredMask)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (requiredMask == 0)
            requiredMask = MaskLeftTrigger;

        for (uint i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetState(i, out XINPUT_STATE state) == ERROR_SUCCESS)
                {
                    uint currentMask = ReadCombinedMask(in state.Gamepad);
                    if ((currentMask & requiredMask) == requiredMask)
                        return true;
                }
            }
            catch
            {
                break;
            }
        }
        return false;
    }

    private static uint ReadCombinedMask(in XINPUT_GAMEPAD pad)
    {
        uint mask = pad.wButtons;
        if (pad.bLeftTrigger >= TriggerThreshold)
            mask |= MaskLeftTrigger;
        if (pad.bRightTrigger >= TriggerThreshold)
            mask |= MaskRightTrigger;
        return mask;
    }
}

/// <summary>
/// Fallback no-op gamepad button backend for platforms where native HID/evdev/IOKit button polling is not yet active.
/// </summary>
public sealed class NullGamepadButtonBackend : IGamepadButtonBackend
{
    public uint ReadAnyControllerMask() => 0;
    public bool IsMaskPressed(uint requiredMask) => false;
}
