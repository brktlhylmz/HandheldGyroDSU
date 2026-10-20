using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HandheldDsuServer.Models;
using Windows.Devices.Sensors;

namespace HandheldDsuServer.Services;

/// <summary>
/// Manages internal WinRT IMU sensors (Gyrometer + Accelerometer),
/// providing high-frequency event-driven sampling, drift bias calibration, and resting pitch compensation.
/// </summary>
public sealed class SensorService : IDisposable
{
    private readonly object _lock = new();
    private DeviceProfile _activeProfile;

    private Gyrometer? _gyrometer;
    private Accelerometer? _accelerometer;
    private Thread? _sensorThread;
    private volatile bool _stopRequested;
    private readonly ManualResetEventSlim _initCompleted = new(false);

    // Raw sensor telemetry updated via WinRT ReadingChanged events
    private float _rawAccX;
    private float _rawAccY;
    private float _rawAccZ = -1.0f;

    private float _rawGyroX;
    private float _rawGyroY;
    private float _rawGyroZ;

    private long _lastGyroEventTimestamp;
    private long _gyroEventsReceived;
    private long _accelEventsReceived;

    // Calibration offsets (Drift Bias and Resting Pitch Attitude)
    private GyroBias _gyroBias = GyroBias.Zero;
    private float _restAccX;
    private float _restAccY;
    private float _restAccZ = -1.0f;
    private volatile bool _usePitchOffset = true;
    private float _sensitivity = 1.0f;
    private float _deadzone;
    private volatile bool _isCalibrating;
    private volatile int _calibrationProgressPercent = 100;

    public bool IsGyroAvailable { get; private set; }
    public bool IsAccelerometerAvailable { get; private set; }
    public uint GyroReportIntervalMs { get; private set; } = 10;
    public uint AccelReportIntervalMs { get; private set; } = 10;
    public long GyroEventsReceived => Interlocked.Read(ref _gyroEventsReceived);
    public long AccelEventsReceived => Interlocked.Read(ref _accelEventsReceived);

    public bool IsCalibrating => _isCalibrating;
    public int CalibrationProgressPercent => _calibrationProgressPercent;
    public GyroBias CurrentBias => _gyroBias;
    public bool UsePitchOffset => _usePitchOffset;
    public float Sensitivity => _sensitivity;
    public float Deadzone => _deadzone;
    public float RestAccX => _restAccX;
    public float RestAccY => _restAccY;
    public float RestAccZ => _restAccZ;

    public DeviceProfile ActiveProfile
    {
        get { lock (_lock) return _activeProfile; }
        set { lock (_lock) _activeProfile = value; }
    }

    public SensorService(DeviceProfile? initialProfile = null, AppSettings? settings = null)
    {
        _activeProfile = initialProfile ?? DeviceProfile.CreateDefault();

        if (settings is not null)
        {
            if (settings.BiasX != 0f || settings.BiasY != 0f || settings.BiasZ != 0f)
            {
                _gyroBias = new GyroBias(settings.BiasX, settings.BiasY, settings.BiasZ);
            }

            _activeProfile.InvertX = settings.InvertX;
            _activeProfile.InvertY = settings.InvertY;
            _activeProfile.InvertZ = settings.InvertZ;
            _activeProfile.SwapAxes = settings.SwapAxes;
            _usePitchOffset = settings.UsePitchOffset;
            _sensitivity = settings.Sensitivity > 0.05f ? Math.Clamp(settings.Sensitivity, 0.2f, 3.0f) : 1.0f;
            _deadzone = Math.Clamp(settings.Deadzone, 0f, 5.0f);

            if (settings.RestAccX != 0f || settings.RestAccY != 0f || settings.RestAccZ != 0f)
            {
                _restAccX = settings.RestAccX;
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
        bool? usePitchOffset = null,
        float? sensitivity = null,
        float? deadzone = null)
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
            if (sensitivity.HasValue)
                _sensitivity = Math.Clamp(sensitivity.Value, 0.2f, 3.0f);
            if (deadzone.HasValue)
                _deadzone = Math.Clamp(deadzone.Value, 0f, 5.0f);

            return _activeProfile;
        }
    }

    public DeviceProfile ResetAxisSettings()
    {
        lock (_lock)
        {
            _activeProfile.ResetToDefaults();
            _usePitchOffset = true;
            _sensitivity = 1.0f;
            _deadzone = 0.0f;
            return _activeProfile;
        }
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
                // Ignore cleanup exceptions on shutdown
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
            double sumAccX = 0;
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
                    sumAccX += _rawAccX;
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
                    _restAccX = (float)(sumAccX / sampleCount);
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

        float rawAx, rawAy, rawAz;
        float rawGx, rawGy, rawGz;
        float restAx, restAy, restAz;
        float sensitivity, deadzone;
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

            restAx = _restAccX;
            restAy = _restAccY;
            restAz = _restAccZ;

            sensitivity = _sensitivity;
            deadzone = _deadzone;

            profile = _activeProfile;
        }

        // When pitch offset compensation is enabled, rotate the Y-Z accelerometer plane
        // so the calibrated holding angle acts as the 0-degree neutral reference (ay = 0, az = -1G)
        if (_usePitchOffset)
        {
            rawAx -= restAx;
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

        // Zero angular velocity if the IMU driver stops firing ReadingChanged while stationary (> 200ms)
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

        // Apply soft-knee deadzone filter and sensitivity gain multiplier
        calGx = ApplyDeadzoneAndSensitivity(calGx, deadzone, sensitivity);
        calGy = ApplyDeadzoneAndSensitivity(calGy, deadzone, sensitivity);
        calGz = ApplyDeadzoneAndSensitivity(calGz, deadzone, sensitivity);

        return profile.Transform(rawAx, rawAy, rawAz, calGx, calGy, calGz, timestampUs);
    }

    private static float ApplyDeadzoneAndSensitivity(float value, float deadzone, float sensitivity)
    {
        float abs = MathF.Abs(value);
        if (abs <= deadzone)
            return 0f;

        float sign = value >= 0f ? 1f : -1f;
        return sign * (abs - deadzone) * sensitivity;
    }

    public void Dispose()
    {
        _stopRequested = true;
        _initCompleted.Dispose();
    }
}
