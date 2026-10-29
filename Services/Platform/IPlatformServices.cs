using System;

namespace HandheldDsuServer.Services.Platform;

/// <summary>
/// Platform-agnostic contract for OS startup registration (Windows Registry, macOS LaunchAgents, Linux XDG Autostart).
/// </summary>
public interface IStartupPlatformBackend
{
    bool IsStartupEnabled();
    bool SetStartup(bool enable, string? customExePath = null);
}

/// <summary>
/// Platform-agnostic contract for low-latency relative mouse cursor movement injection
/// (Windows SendInput, macOS CoreGraphics CGEvent, Linux /dev/uinput).
/// </summary>
public interface IMouseInjectionBackend
{
    void SendRelativeMouseMove(int dx, int dy);
}

/// <summary>
/// Platform-agnostic contract for reading physical gamepad button bitmasks for hotkeys and triggers
/// (Windows XInput, macOS GameController/IOKit, Linux evdev/hidraw).
/// </summary>
public interface IGamepadButtonBackend
{
    uint ReadAnyControllerMask();
    bool IsMaskPressed(uint requiredMask);
}

/// <summary>
/// Platform-agnostic contract for raw 6-axis IMU hardware telemetry providers
/// (Windows WinRT Sensors, Linux Industrial I/O, Raw HID controllers).
/// </summary>
public interface IImuSensorBackend : IDisposable
{
    bool IsGyroAvailable { get; }
    bool IsAccelerometerAvailable { get; }
    uint GyroReportIntervalMs { get; }
    uint AccelReportIntervalMs { get; }
    void RequestRebind();
}
