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
        bool invertY = false,
        bool invertZ = false,
        bool swapAxes = false)
    {
        InvertX = invertX;
        InvertY = invertY;
        InvertZ = invertZ;
        SwapAxes = swapAxes;
    }

    public static DeviceProfile CreateDefault() => new(
        invertX: false,
        invertY: false,
        invertZ: false,
        swapAxes: false
    );

    public void ResetToDefaults()
    {
        InvertX = false;
        InvertY = false;
        InvertZ = false;
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

        // Base mapping from WinRT landscape sensor coordinates (+X Right, +Y Up, +Z Out of screen)
        // into Cemuhook DSU v1001 standard coordinates:
        // - AccX      : -ax (Tilting right yields rawAccX > 0 -> DSU AccX < 0 / Accel Right > 0)
        // - AccY      : +az (Resting flat gravity is -1.0G -> DSU AccY = -1.0G / Accel Up > 0)
        // - AccZ      : +ay (Tilting nose up yields rawAccY < 0 -> DSU AccZ < 0 / Accel Forward > 0)
        // - GyroPitch : +gx (Pitching nose up yields rawGyroX > 0 -> DSU GyroPitch > 0)
        // - GyroYaw   : +gz (Turning left CCW yields rawGyroZ > 0 -> DSU GyroYaw > 0)
        // - GyroRoll  : +gy (Rolling right CW yields rawGyroY > 0 -> DSU GyroRoll > 0)
        float dsuAccX = -ax;
        float dsuAccY = az;
        float dsuAccZ = ay;
        float dsuPitch = gx;
        float dsuYaw = gz;
        float dsuRoll = gy;

        // Apply user-configured per-axis inversions symmetrically to paired gyro + gravity axes:
        // - InvertX ("Invert Pitch (X)") inverts both Pitch gyro and Pitch gravity (AccZ)
        // - InvertY ("Invert Roll (Y)") inverts both Roll gyro and Roll gravity (AccX)
        // - InvertZ ("Invert Yaw (Z)") inverts only Yaw gyro; vertical gravity (AccY = -1G) is preserved
        if (InvertX)
        {
            dsuPitch = -dsuPitch;
            dsuAccZ = -dsuAccZ;
        }

        if (InvertY)
        {
            dsuRoll = -dsuRoll;
            dsuAccX = -dsuAccX;
        }

        if (InvertZ)
        {
            dsuYaw = -dsuYaw;
        }

        return new MotionData(
            AccX: dsuAccX,
            AccY: dsuAccY,
            AccZ: dsuAccZ,
            GyroPitch: dsuPitch,
            GyroYaw: dsuYaw,
            GyroRoll: dsuRoll,
            TimestampUs: timestampUs
        );
    }
}
