namespace HandheldDsuServer.Models;

/// <summary>
/// Manages axis mapping and orientation inversions for different Windows handhelds
/// (Asus ROG Ally, Lenovo Legion Go, MSI Claw) according to their internal IMU sensor mounting.
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
    /// Default axis profile for Asus ROG Ally / ROG Ally X (Landscape).
    /// </summary>
    public static DeviceProfile CreateAsusRogAlly() => new(
        name: "Asus ROG Ally",
        invertX: false,
        invertY: true,
        invertZ: true,
        swapAxes: false
    );

    /// <summary>
    /// Axis profile for Lenovo Legion Go (Portrait-native display sensor layout).
    /// </summary>
    public static DeviceProfile CreateLenovoLegionGo() => new(
        name: "Lenovo Legion Go",
        invertX: true,
        invertY: false,
        invertZ: true,
        swapAxes: true
    );

    /// <summary>
    /// Axis profile for MSI Claw.
    /// </summary>
    public static DeviceProfile CreateMsiClaw() => new(
        name: "MSI Claw",
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
