namespace HandheldDsuServer.Models;

/// <summary>
/// Manages axis mapping and orientation inversions for Windows handhelds
/// according to their internal IMU sensor mounting orientation.
/// </summary>
public sealed class DeviceProfile
{
    public string Name { get; set; }
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
    public bool InvertZ { get; set; }
    public bool SwapAxes { get; set; }

    public DeviceProfile(
        string name,
        bool invertX = false,
        bool invertY = true,
        bool invertZ = true,
        bool swapAxes = false)
    {
        Name = name;
        InvertX = invertX;
        InvertY = invertY;
        InvertZ = invertZ;
        SwapAxes = swapAxes;
    }

    /// <summary>
    /// Default axis profile for standard landscape-mounted IMU sensors (ASUS, Acer, Ayaneo, GPD, etc.).
    /// </summary>
    public static DeviceProfile CreateLandscapeStandard() => new(
        name: "Standard Landscape",
        invertX: false,
        invertY: true,
        invertZ: true,
        swapAxes: false
    );

    /// <summary>
    /// Axis profile for handhelds with portrait-native display/sensor mounting (e.g., Lenovo Legion Go).
    /// </summary>
    public static DeviceProfile CreatePortraitNative() => new(
        name: "Portrait Sensor",
        invertX: true,
        invertY: false,
        invertZ: true,
        swapAxes: true
    );

    /// <summary>
    /// Alternative landscape axis profile (e.g., MSI Claw / Inverted Y-axis sensors).
    /// </summary>
    public static DeviceProfile CreateAlternativeLandscape() => new(
        name: "Alternative Landscape",
        invertX: false,
        invertY: false,
        invertZ: true,
        swapAxes: false
    );

    /// <summary>
    /// Transforms raw WinRT sensor readings (Accelerometer in G and Gyrometer in deg/s)
    /// into the active device profile and Cemuhook DSU coordinate space.
    /// </summary>
    public MotionData Transform(
        float rawAccX, float rawAccY, float rawAccZ,
        float calGyroX, float calGyroY, float calGyroZ,
        ulong timestampUs)
    {
        float ax = rawAccX;
        float ay = rawAccY;
        float az = rawAccZ;

        float gx = calGyroX;
        float gy = calGyroY;
        float gz = calGyroZ;

        // Swap X and Y axes for handhelds with portrait-oriented sensor mounting
        if (SwapAxes)
        {
            (ax, ay) = (ay, ax);
            (gx, gy) = (gy, gx);
        }

        // Apply per-axis inversions
        if (InvertX)
        {
            ax = -ax;
            gx = -gx;
        }

        if (InvertY)
        {
            ay = -ay;
            gy = -gy;
        }

        if (InvertZ)
        {
            az = -az;
            gz = -gz;
        }

        // Cemuhook DSU standard coordinate mapping:
        // Pitch = rotation around X-axis
        // Yaw   = rotation around Z-axis
        // Roll  = rotation around Y-axis
        return new MotionData(
            AccX: ax,
            AccY: az,
            AccZ: ay,
            GyroPitch: gx,
            GyroYaw: gz,
            GyroRoll: gy,
            TimestampUs: timestampUs
        );
    }
}
