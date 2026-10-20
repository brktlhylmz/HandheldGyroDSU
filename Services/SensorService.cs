using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HandheldDsuServer.Models;
using Windows.Devices.Sensors;

namespace HandheldDsuServer.Services;

/// <summary>
/// Windows.Devices.Sensors (WinRT) API'lerini kullanarak dahili IMU (Bosch BOSC0200 / Gyrometer + Accelerometer)
/// sensörlerini yöneten, yüksek yenileme hızı sağlayan ve Drift Bias kalibrasyonu yapan servis.
/// </summary>
public sealed class SensorService : IDisposable
{
    private readonly object _lock = new();
    private readonly DeviceProfile[] _presets;
    private int _currentPresetIndex;
    private DeviceProfile _activeProfile;

    private Gyrometer? _gyrometer;
    private Accelerometer? _accelerometer;
    private Thread? _sensorThread;
    private volatile bool _stopRequested;
    private readonly ManualResetEventSlim _initCompleted = new(false);

    // Ham sensör verileri (WinRT ReadingChanged event-driven)
    private float _rawAccX;
    private float _rawAccY;
    private float _rawAccZ = -1.0f;

    private float _rawGyroX;
    private float _rawGyroY;
    private float _rawGyroZ;

    private long _lastGyroEventTimestamp;
    private long _gyroEventsReceived;
    private long _accelEventsReceived;

    // Kalibrasyon (Drift Bias ve Tutuş Açısı) sapma değerleri
    private GyroBias _gyroBias = GyroBias.Zero;
    private float _restAccY;
    private float _restAccZ = -1.0f;
    private volatile bool _usePitchOffset = true;
    private volatile bool _isCalibrating;
    private volatile int _calibrationProgressPercent = 100;

    // Emülatör (Eden/Yuzu) "Motion 1 -> Shake!" eşleştirmesi için sanal sallama zamanlayıcısı
    private long _shakeUntilTimestamp;

    public bool IsGyroAvailable { get; private set; }
    public bool IsAccelerometerAvailable { get; private set; }
    public uint GyroReportIntervalMs { get; private set; } = 10;
    public uint AccelReportIntervalMs { get; private set; } = 10;
    public long GyroEventsReceived => Interlocked.Read(ref _gyroEventsReceived);
    public long AccelEventsReceived => Interlocked.Read(ref _accelEventsReceived);

    public bool IsCalibrating => _isCalibrating;
    public int CalibrationProgressPercent => _calibrationProgressPercent;
    public bool IsSimulatingShake => Stopwatch.GetTimestamp() < Interlocked.Read(ref _shakeUntilTimestamp);
    public GyroBias CurrentBias => _gyroBias;
    public bool UsePitchOffset => _usePitchOffset;
    public float RestAccY => _restAccY;
    public float RestAccZ => _restAccZ;
    public IReadOnlyList<DeviceProfile> Presets => _presets;

    public DeviceProfile ActiveProfile
    {
        get { lock (_lock) return _activeProfile; }
        set { lock (_lock) _activeProfile = value; }
    }

    public SensorService(DeviceProfile? initialProfile = null, AppSettings? settings = null)
    {
        _presets =
        [
            DeviceProfile.CreateAsusRogAlly(),
            DeviceProfile.CreateLenovoLegionGo(),
            DeviceProfile.CreateMsiClaw()
        ];
        _currentPresetIndex = 0;
        _activeProfile = initialProfile ?? _presets[0];

        if (settings is not null)
        {
            if (settings.BiasX != 0f || settings.BiasY != 0f || settings.BiasZ != 0f)
            {
                _gyroBias = new GyroBias(settings.BiasX, settings.BiasY, settings.BiasZ);
            }
            if (!string.IsNullOrWhiteSpace(settings.ActiveProfileKey))
            {
                SetOrCycleProfile(settings.ActiveProfileKey);
            }
            if (settings.InvertX.HasValue)
                _activeProfile.InvertX = settings.InvertX.Value;
            if (settings.InvertY.HasValue)
                _activeProfile.InvertY = settings.InvertY.Value;
            if (settings.InvertZ.HasValue)
                _activeProfile.InvertZ = settings.InvertZ.Value;
            if (settings.SwapAxes.HasValue)
                _activeProfile.SwapAxes = settings.SwapAxes.Value;

            _usePitchOffset = settings.UsePitchOffset;
            if (settings.RestAccY != 0f || settings.RestAccZ != 0f)
            {
                _restAccY = settings.RestAccY;
                _restAccZ = settings.RestAccZ;
            }
        }
    }

    public DeviceProfile UpdateAxisSettings(
        bool? invertX = null,
        bool? invertY = null,
        bool? invertZ = null,
        bool? swapAxes = null,
        bool? usePitchOffset = null)
    {
        lock (_lock)
        {
            if (invertX.HasValue)
                _activeProfile.InvertX = invertX.Value;
            if (invertY.HasValue)
                _activeProfile.InvertY = invertY.Value;
            if (invertZ.HasValue)
                _activeProfile.InvertZ = invertZ.Value;
            if (swapAxes.HasValue)
                _activeProfile.SwapAxes = swapAxes.Value;
            if (usePitchOffset.HasValue)
                _usePitchOffset = usePitchOffset.Value;

            return _activeProfile;
        }
    }

    public static string GetProfileKey(DeviceProfile profile)
    {
        if (profile.Name.Contains("Legion", StringComparison.OrdinalIgnoreCase))
            return "legion_go";
        if (profile.Name.Contains("Claw", StringComparison.OrdinalIgnoreCase))
            return "msi_claw";
        return "rog_ally";
    }

    public DeviceProfile SetOrCycleProfile(string? profileKey = null)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(profileKey))
            {
                _currentPresetIndex = (_currentPresetIndex + 1) % _presets.Length;
                DeviceProfile p = _presets[_currentPresetIndex];
                _activeProfile = new DeviceProfile(p.Name, p.InvertX, p.InvertY, p.InvertZ, p.SwapAxes);
                return _activeProfile;
            }

            string key = profileKey.Trim().ToLowerInvariant();
            if (key.Contains("ally") || key == "0")
                _currentPresetIndex = 0;
            else if (key.Contains("legion") || key == "1")
                _currentPresetIndex = 1;
            else if (key.Contains("claw") || key == "2")
                _currentPresetIndex = 2;
            else
                _currentPresetIndex = (_currentPresetIndex + 1) % _presets.Length;

            DeviceProfile preset = _presets[_currentPresetIndex];
            _activeProfile = new DeviceProfile(preset.Name, preset.InvertX, preset.InvertY, preset.InvertZ, preset.SwapAxes);
            return _activeProfile;
        }
    }

    /// <summary>
    /// Emülatörlerde (Eden, Yuzu, Ryujinx) "Motion 1 -> Shake!" atamasını mouse'a takılmadan
    /// kolayca yapabilmek için belirtilen süre boyunca güçlü bir sallama (Shake) sinyali üretir.
    /// </summary>
    public void TriggerShake(int durationMs = 4500)
    {
        long targetTicks = Stopwatch.GetTimestamp() + (long)(durationMs / 1000.0 * Stopwatch.Frequency);
        Interlocked.Exchange(ref _shakeUntilTimestamp, targetTicks);
    }

    public void Initialize()
    {
        if (_sensorThread is not null)
            return;

        _stopRequested = false;
        _sensorThread = new Thread(SensorWorkerLoop)
        {
            Name = "WinRT-IMU-EventThread",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _sensorThread.SetApartmentState(ApartmentState.MTA);
        _sensorThread.Start();

        _initCompleted.Wait(TimeSpan.FromMilliseconds(2500));
    }

    private void SensorWorkerLoop()
    {
        try
        {
            try
            {
                _gyrometer = Gyrometer.GetDefault();
                if (_gyrometer is not null)
                {
                    uint minGyro = _gyrometer.MinimumReportInterval;
                    GyroReportIntervalMs = Math.Clamp(minGyro, 4u, 16u);
                    _gyrometer.ReportInterval = GyroReportIntervalMs;
                    _gyrometer.ReadingChanged += OnGyrometerReadingChanged;
                    IsGyroAvailable = true;
                }
            }
            catch
            {
                _gyrometer = null;
            }

            try
            {
                _accelerometer = Accelerometer.GetDefault();
                if (_accelerometer is not null)
                {
                    uint minAccel = _accelerometer.MinimumReportInterval;
                    AccelReportIntervalMs = Math.Clamp(minAccel, 4u, 16u);
                    _accelerometer.ReportInterval = AccelReportIntervalMs;
                    _accelerometer.ReadingChanged += OnAccelerometerReadingChanged;
                    IsAccelerometerAvailable = true;
                }
            }
            catch
            {
                _accelerometer = null;
            }

            _initCompleted.Set();

            while (!_stopRequested)
            {
                Thread.Sleep(50);
            }
        }
        finally
        {
            _initCompleted.Set();
            try
            {
                if (_gyrometer is not null)
                {
                    _gyrometer.ReadingChanged -= OnGyrometerReadingChanged;
                    _gyrometer.ReportInterval = 0;
                }
                if (_accelerometer is not null)
                {
                    _accelerometer.ReadingChanged -= OnAccelerometerReadingChanged;
                    _accelerometer.ReportInterval = 0;
                }
            }
            catch
            {
                // Yoksay
            }
        }
    }

    private void OnGyrometerReadingChanged(Gyrometer sender, GyrometerReadingChangedEventArgs args)
    {
        GyrometerReading reading = args.Reading;
        lock (_lock)
        {
            _rawGyroX = (float)reading.AngularVelocityX;
            _rawGyroY = (float)reading.AngularVelocityY;
            _rawGyroZ = (float)reading.AngularVelocityZ;
        }
        Interlocked.Exchange(ref _lastGyroEventTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _gyroEventsReceived);
    }

    private void OnAccelerometerReadingChanged(Accelerometer sender, AccelerometerReadingChangedEventArgs args)
    {
        AccelerometerReading reading = args.Reading;
        lock (_lock)
        {
            _rawAccX = (float)reading.AccelerationX;
            _rawAccY = (float)reading.AccelerationY;
            _rawAccZ = (float)reading.AccelerationZ;
        }
        Interlocked.Increment(ref _accelEventsReceived);
    }

    public async Task CalibrateAsync(CancellationToken cancellationToken = default)
    {
        if (_isCalibrating)
            return;

        _isCalibrating = true;
        _calibrationProgressPercent = 0;

        try
        {
            const int durationMs = 1000;
            const int sampleIntervalMs = 20;
            int totalSteps = durationMs / sampleIntervalMs;

            double sumX = 0;
            double sumY = 0;
            double sumZ = 0;
            double sumAccY = 0;
            double sumAccZ = 0;
            int sampleCount = 0;

            for (int step = 0; step < totalSteps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (_lock)
                {
                    sumX += _rawGyroX;
                    sumY += _rawGyroY;
                    sumZ += _rawGyroZ;
                    sumAccY += _rawAccY;
                    sumAccZ += _rawAccZ;
                }

                sampleCount++;
                _calibrationProgressPercent = (int)((step + 1) * 100.0 / totalSteps);

                await Task.Delay(sampleIntervalMs, cancellationToken);
            }

            if (sampleCount > 0)
            {
                _gyroBias = new GyroBias(
                    BiasX: (float)(sumX / sampleCount),
                    BiasY: (float)(sumY / sampleCount),
                    BiasZ: (float)(sumZ / sampleCount)
                );

                lock (_lock)
                {
                    _restAccY = (float)(sumAccY / sampleCount);
                    _restAccZ = (float)(sumAccZ / sampleCount);
                }
            }
        }
        finally
        {
            _calibrationProgressPercent = 100;
            _isCalibrating = false;
        }
    }

    public MotionData GetProcessedMotionData()
    {
        long nowTicks = Stopwatch.GetTimestamp();
        ulong timestampUs = (ulong)(nowTicks * 1_000_000.0 / Stopwatch.Frequency);

        // Eğer emülatör eşleştirmesi için Shake simülasyonu aktifse güçlü bir sallama dalgası üret:
        if (nowTicks < Interlocked.Read(ref _shakeUntilTimestamp))
        {
            double t = nowTicks / (double)Stopwatch.Frequency;
            float wave = MathF.Sin((float)(t * 35.0)); // ~5.5 Hz hızlı sallama
            return new MotionData(
                AccX: wave * 2.8f,
                AccY: -1.0f + (wave * 1.8f),
                AccZ: wave * 2.2f,
                GyroPitch: wave * 340.0f,
                GyroYaw: -wave * 280.0f,
                GyroRoll: wave * 260.0f,
                TimestampUs: timestampUs
            );
        }

        float rawAx, rawAy, rawAz;
        float rawGx, rawGy, rawGz;
        float restAy, restAz;
        DeviceProfile profile;
        GyroBias bias = _gyroBias;

        lock (_lock)
        {
            rawAx = _rawAccX;
            rawAy = _rawAccY;
            rawAz = _rawAccZ;

            rawGx = _rawGyroX;
            rawGy = _rawGyroY;
            rawGz = _rawGyroZ;

            restAy = _restAccY;
            restAz = _restAccZ;

            profile = _activeProfile;
        }

        // Eğer "Tutuş Açısını Merkezle (Pitch Offset)" açıksa, kalibrasyon anındaki
        // tutuş açısını 0° düz referans (ay=0, az=-1G) kabul edecek şekilde Y-Z düzlemini döndür:
        if (_usePitchOffset)
        {
            float mag = MathF.Sqrt((restAy * restAy) + (restAz * restAz));
            if (mag > 0.2f)
            {
                float sinT = restAy / mag;
                float cosT = -restAz / mag;
                float rotatedAy = (rawAy * cosT) + (rawAz * sinT);
                float rotatedAz = -(rawAy * sinT) + (rawAz * cosT);
                rawAy = rotatedAy;
                rawAz = rotatedAz;
            }
        }

        float calGx = rawGx - bias.BiasX;
        float calGy = rawGy - bias.BiasY;
        float calGz = rawGz - bias.BiasZ;

        // Cihaz hareketsizken Bosch BOSC0200 yeni ReadingChanged göndermediğinde
        // son açısal hızın takılı kalmasını önle (200ms hareketsizlik sonrası sıfırla):
        long lastGyroTicks = Interlocked.Read(ref _lastGyroEventTimestamp);
        if (lastGyroTicks > 0)
        {
            double elapsedMs = (nowTicks - lastGyroTicks) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs > 200.0)
            {
                calGx = 0f;
                calGy = 0f;
                calGz = 0f;
            }
        }

        return profile.Transform(rawAx, rawAy, rawAz, calGx, calGy, calGz, timestampUs);
    }

    public void Dispose()
    {
        _stopRequested = true;
        _initCompleted.Dispose();
    }
}
