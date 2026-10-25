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

        // Apply per-axis inversions:
        // - InvertX ("Invert Pitch (X)") inverts both Pitch gyro (gx) and Pitch gravity (ay -> AccZ)
        // - InvertY ("Invert Roll (Y)") inverts both Roll gyro (gy) and Roll gravity (ax -> AccX)
        // - InvertZ ("Invert Yaw (Z)") inverts only Yaw gyro (gz); vertical gravity (az -> AccY) stays -1G
        if (InvertX)
        {
            ay = -ay;
            gx = -gx;
        }

        if (InvertY)
        {
            ax = -ax;
            gy = -gy;
        }

        if (InvertZ)
        {
            gz = -gz;
        }

        // Cemuhook DSU standard coordinate mapping:
        // AccY resting gravity is -1.0G (so Dolphin's Accel Up = -AccY * 9.81 = +9.81 m/s^2)
        // Pitch = rotation around X-axis (paired with Y-axis gravity -> AccZ)
        // Yaw   = rotation around Z-axis
        // Roll  = rotation around Y-axis (paired with X-axis gravity -> AccX)
        return new MotionData(
            AccX: -ax,
            AccY: az,
            AccZ: -ay,
            GyroPitch: -gx,
            GyroYaw: gz,
            GyroRoll: gy,
            TimestampUs: timestampUs
        );
    }
}
