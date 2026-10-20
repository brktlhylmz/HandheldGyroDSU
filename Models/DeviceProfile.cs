namespace HandheldDsuServer.Models;

/// <summary>
/// Farklı el konsollarının (Asus ROG Ally, Lenovo Legion Go, MSI Claw vb.)
/// IMU sensör montaj yönlerine göre eksen dönüşümlerini yöneten yapılandırma sınıfı.
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
    /// Asus ROG Ally / ROG Ally X için varsayılan eksen profili (Landscape).
    /// </summary>
    public static DeviceProfile CreateAsusRogAlly() => new(
        name: "Asus ROG Ally (Varsayılan)",
        invertX: false,
        invertY: true,
        invertZ: true,
        swapAxes: false
    );

    /// <summary>
    /// Lenovo Legion Go (Dikey/Portrait panel tabanlı sensör yerleşimi) eksen profili.
    /// </summary>
    public static DeviceProfile CreateLenovoLegionGo() => new(
        name: "Lenovo Legion Go",
        invertX: true,
        invertY: false,
        invertZ: true,
        swapAxes: true
    );

    /// <summary>
    /// MSI Claw eksen profili.
    /// </summary>
    public static DeviceProfile CreateMsiClaw() => new(
        name: "MSI Claw",
        invertX: false,
        invertY: false,
        invertZ: true,
        swapAxes: false
    );

    /// <summary>
    /// Ham WinRT sensör verilerini (Accelerometer G ve Gyrometer deg/s)
    /// aktif cihaz profiline ve Cemuhook DSU koordinat sistemine dönüştürür.
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

        // Farklı ekran/anakart montaj yönüne sahip konsollar için X ve Y eksenlerini takas et
        if (SwapAxes)
        {
            (ax, ay) = (ay, ax);
            (gx, gy) = (gy, gx);
        }

        // Eksen yönü ters çevirme (Invert) ayarlarını uygula
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

        // Cemuhook DSU standardı:
        // Pitch = X ekseni etrafında dönüş
        // Yaw   = Z ekseni etrafında dönüş
        // Roll  = Y ekseni etrafında dönüş
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
