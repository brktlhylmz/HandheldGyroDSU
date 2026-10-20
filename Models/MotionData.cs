namespace HandheldDsuServer.Models;

/// <summary>
/// Cemuhook DSU protokolüne gönderilecek işlenmiş hareket (İvmeölçer + Jiroskop) verilerini temsil eder.
/// </summary>
public readonly record struct MotionData(
    float AccX,         // G cinsinden ivme (X)
    float AccY,         // G cinsinden ivme (Y)
    float AccZ,         // G cinsinden ivme (Z)
    float GyroPitch,    // deg/s cinsinden açısal hız (Pitch / X)
    float GyroYaw,      // deg/s cinsinden açısal hız (Yaw / Z)
    float GyroRoll,     // deg/s cinsinden açısal hız (Roll / Y)
    ulong TimestampUs   // Mikrosaniye (µs) cinsinden zaman damgası
);

/// <summary>
/// Kalibrasyon sonucu hesaplanan sıfır noktası sapma (Drift Bias) değerleri.
/// </summary>
public readonly record struct GyroBias(
    float BiasX,
    float BiasY,
    float BiasZ
)
{
    public static readonly GyroBias Zero = new(0f, 0f, 0f);
}
