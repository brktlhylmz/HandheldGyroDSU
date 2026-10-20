namespace HandheldDsuServer.Models;

/// <summary>
/// Manages axis mapping and orientation inversions for Windows handheld IMU sensors,
/// transforming raw WinRT sensor readings into the Cemuhook DSU coordinate space.
/// </summary>
public sealed class DeviceProfile
{
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
    public bool InvertZ { get; set; }
    public bool SwapAxes { get; set; }

    public DeviceProfile(
        bool invertX = false,
        bool invertY = true,
        bool invertZ = true,
        bool swapAxes = false)
    {
        InvertX = invertX;
        InvertY = invertY;
        InvertZ = invertZ;
        SwapAxes = swapAxes;
    }

    public static DeviceProfile CreateDefault() => new(
        invertX: false,
        invertY: true,
        invertZ: true,
        swapAxes: false
    );

    public void ResetToDefaults()
    {
        InvertX = false;
        InvertY = true;
        InvertZ = true;
        SwapAxes = false;
    }

    /// <summary>
    /// Transforms raw WinRT sensor readings (Accelerometer in G and Gyrometer in deg/s)
    /// into the Cemuhook DSU coordinate space.
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
