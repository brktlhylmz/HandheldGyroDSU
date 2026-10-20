namespace HandheldDsuServer.Models;

/// <summary>
/// Represents processed motion telemetry (Accelerometer + Gyroscope) to be transmitted over the Cemuhook DSU protocol.
/// </summary>
public readonly record struct MotionData(
    float AccX,         // Linear acceleration in G (X-axis)
    float AccY,         // Linear acceleration in G (Y-axis)
    float AccZ,         // Linear acceleration in G (Z-axis)
    float GyroPitch,    // Angular velocity in deg/s (Pitch / X-axis)
    float GyroYaw,      // Angular velocity in deg/s (Yaw / Z-axis)
    float GyroRoll,     // Angular velocity in deg/s (Roll / Y-axis)
    ulong TimestampUs   // Timestamp in microseconds (µs)
);

/// <summary>
/// Represents zero-rate gyroscope drift bias offsets computed during calibration.
/// </summary>
public readonly record struct GyroBias(
    float BiasX,
    float BiasY,
    float BiasZ
)
{
    public static readonly GyroBias Zero = new(0f, 0f, 0f);
}
