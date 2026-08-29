using System.Buffers.Binary;
using HidSharp;

internal static class DeviceConstants
{
    public const int VendorId = 0x413D;
    public const int ProductId = 0x2107;
}

internal sealed record Measurement(double TemperatureC, double HumidityPercent);

internal sealed record SensorSnapshot(
    double TemperatureC,
    double HumidityPercent,
    double DewPointC,
    DateTimeOffset UpdatedAt);

internal sealed class SensorService : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    private TemperHumReader? _reader;
    private SensorSnapshot? _snapshot;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;
    private bool _connected;
    private bool _connecting;
    private bool _desiredConnected;
    private string? _lastError;

    public SensorService(AppConfig config)
    {
        _config = config;
        _desiredConnected = config.AutoConnect;
    }

    public bool Connected
    {
        get
        {
            lock (_stateGate)
            {
                return _connected;
            }
        }
    }

    public bool Connecting
    {
        get
        {
            lock (_stateGate)
            {
                return _connecting;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_stateGate)
            {
                return _lastError;
            }
        }
    }

    public SensorSnapshot Snapshot
    {
        get
        {
            lock (_stateGate)
            {
                if (!_connected)
                {
                    throw new AlpacaDeviceException(AlpacaErrors.NotConnected, "The TEMPerHUM sensor is not connected.");
                }

                if (_snapshot is null)
                {
                    throw new AlpacaDeviceException(AlpacaErrors.DriverError, "No TEMPerHUM measurement is available yet.");
                }

                return _snapshot;
            }
        }
    }

    public void StartAutoReconnect()
    {
        lock (_stateGate)
        {
            if (_reconnectTask is not null)
            {
                return;
            }

            _reconnectCts = new CancellationTokenSource();
            var token = _reconnectCts.Token;
            _reconnectTask = Task.Run(() => ReconnectLoopAsync(token), token);
        }
    }

    public async Task ConnectNowAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            _desiredConnected = true;

            if (_connected || _connecting)
            {
                return;
            }

            _connecting = true;
            _lastError = null;
        }

        TemperHumReader? reader = null;
        try
        {
            reader = TemperHumReader.Open();
            var snapshot = await ReadSnapshotAsync(reader, cancellationToken);

            CancellationTokenSource? oldPollCts = null;
            bool keepConnection;
            lock (_stateGate)
            {
                keepConnection = _desiredConnected;
                if (!keepConnection)
                {
                    _connecting = false;
                }
                else
                {
                    _reader = reader;
                    reader = null;
                    _snapshot = snapshot;
                    _connected = true;
                    _connecting = false;
                    _lastError = null;

                    oldPollCts = _pollCts;
                    var pollCts = new CancellationTokenSource();
                    _pollCts = pollCts;
                    _pollTask = Task.Run(() => PollLoopAsync(pollCts.Token));
                }
            }

            oldPollCts?.Cancel();
            oldPollCts?.Dispose();
            reader?.Dispose();
        }
        catch (Exception ex)
        {
            reader?.Dispose();
            lock (_stateGate)
            {
                _connected = false;
                _connecting = false;
                _lastError = ex.Message;
            }

            throw;
        }
    }

    public void BeginConnect()
    {
        lock (_stateGate)
        {
            _desiredConnected = true;
            if (_connected || _connecting)
            {
                return;
            }

            _connecting = true;
            _lastError = null;
        }

        _ = Task.Run(async () =>
        {
            lock (_stateGate)
            {
                _connecting = false;
            }

            try
            {
                await ConnectNowAsync();
            }
            catch
            {
                // LastError is populated by ConnectNowAsync and the recovery loop
                // will retry while the desired state remains connected.
            }
        });
    }

    public async Task DisconnectNowAsync()
    {
        CancellationTokenSource? pollCts;
        Task? pollTask;

        lock (_stateGate)
        {
            _desiredConnected = false;

            if (!_connected && !_connecting)
            {
                return;
            }

            _connecting = true;
            pollCts = _pollCts;
            pollTask = _pollTask;
            _pollCts = null;
            _pollTask = null;
        }

        pollCts?.Cancel();
        if (pollTask is not null && Task.CurrentId != pollTask.Id)
        {
            try
            {
                await pollTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        pollCts?.Dispose();

        await _ioGate.WaitAsync();
        try
        {
            TemperHumReader? reader;
            lock (_stateGate)
            {
                reader = _reader;
                _reader = null;
                _snapshot = null;
                _connected = false;
                _connecting = false;
            }

            reader?.Dispose();
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public void BeginDisconnect()
    {
        lock (_stateGate)
        {
            _desiredConnected = false;

            if ((!_connected && !_connecting) || _connecting)
            {
                return;
            }

            _connecting = true;
        }

        _ = Task.Run(async () =>
        {
            lock (_stateGate)
            {
                _connecting = false;
            }

            try
            {
                await DisconnectNowAsync();
            }
            catch (Exception ex)
            {
                lock (_stateGate)
                {
                    _connecting = false;
                    _lastError = ex.Message;
                }
            }
        });
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        TemperHumReader reader;
        lock (_stateGate)
        {
            if (!_connected || _reader is null)
            {
                throw new AlpacaDeviceException(AlpacaErrors.NotConnected, "The TEMPerHUM sensor is not connected.");
            }

            reader = _reader;
        }

        var snapshot = await ReadSnapshotAsync(reader, cancellationToken);
        lock (_stateGate)
        {
            _snapshot = snapshot;
            _lastError = null;
        }
    }

    public double TimeSinceLastUpdate(string sensorName)
    {
        ValidateSensorName(sensorName, allowEmpty: true);
        var snapshot = Snapshot;
        return Math.Max(0.0, (DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalSeconds);
    }

    public string SensorDescription(string sensorName)
    {
        ValidateSensorName(sensorName, allowEmpty: false);

        return sensorName.ToLowerInvariant() switch
        {
            "temperature" => "PCsensor TEMPerHUM/TEMPerX USB temperature sensor",
            "humidity" => "PCsensor TEMPerHUM/TEMPerX USB humidity sensor",
            "dewpoint" => "Calculated from calibrated temperature and relative humidity",
            _ => throw new AlpacaDeviceException(AlpacaErrors.NotImplemented, $"Sensor '{sensorName}' is not implemented.")
        };
    }

    private static void ValidateSensorName(string sensorName, bool allowEmpty)
    {
        if (allowEmpty && string.IsNullOrEmpty(sensorName))
        {
            return;
        }

        var validNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CloudCover", "DewPoint", "Humidity", "Pressure", "RainRate",
            "SkyBrightness", "SkyQuality", "SkyTemperature", "StarFWHM",
            "Temperature", "WindDirection", "WindGust", "WindSpeed"
        };

        if (!validNames.Contains(sensorName))
        {
            throw new AlpacaDeviceException(AlpacaErrors.InvalidValue, $"'{sensorName}' is not a valid ObservingConditions sensor name.");
        }

        if (!sensorName.Equals("Temperature", StringComparison.OrdinalIgnoreCase) &&
            !sensorName.Equals("Humidity", StringComparison.OrdinalIgnoreCase) &&
            !sensorName.Equals("DewPoint", StringComparison.OrdinalIgnoreCase))
        {
            throw new AlpacaDeviceException(AlpacaErrors.NotImplemented, $"Sensor '{sensorName}' is not implemented.");
        }
    }

    private async Task<SensorSnapshot> ReadSnapshotAsync(TemperHumReader reader, CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            var raw = reader.ReadMeasurement();
            var temperature = raw.TemperatureC + _config.TemperatureOffsetC;
            var humidity = Math.Clamp(raw.HumidityPercent + _config.HumidityOffsetPercent, 0.0, 100.0);
            var dewPoint = DewPoint.Calculate(temperature, humidity);
            return new SensorSnapshot(temperature, humidity, dewPoint, DateTimeOffset.UtcNow);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _config.PollIntervalSeconds)));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await RefreshAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                HandleConnectionLoss(ex);
                break;
            }
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool shouldConnect;
            lock (_stateGate)
            {
                shouldConnect = _desiredConnected && !_connected && !_connecting;
            }

            if (shouldConnect)
            {
                try
                {
                    await ConnectNowAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // LastError is retained by ConnectNowAsync. Try again after the interval.
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _config.ReconnectIntervalSeconds)), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void HandleConnectionLoss(Exception ex)
    {
        TemperHumReader? reader;
        lock (_stateGate)
        {
            reader = _reader;
            _reader = null;
            _snapshot = null;
            _connected = false;
            _connecting = false;
            _lastError = ex.Message;
        }

        reader?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? reconnectCts;
        Task? reconnectTask;
        lock (_stateGate)
        {
            _desiredConnected = false;
            reconnectCts = _reconnectCts;
            reconnectTask = _reconnectTask;
            _reconnectCts = null;
            _reconnectTask = null;
        }

        reconnectCts?.Cancel();
        if (reconnectTask is not null)
        {
            try
            {
                await reconnectTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        reconnectCts?.Dispose();

        await DisconnectNowAsync();
        _ioGate.Dispose();
    }
}

internal sealed class TemperHumReader : IDisposable
{
    private static readonly byte[] MeasurementCommand = [0x01, 0x80, 0x33, 0x01, 0x00, 0x00, 0x00, 0x00];

    private readonly HidStream _stream;
    private readonly HidDevice _device;

    private TemperHumReader(HidDevice device, HidStream stream)
    {
        _device = device;
        _stream = stream;
        _stream.ReadTimeout = 1200;
        _stream.WriteTimeout = 1200;
    }

    public string DevicePath => _device.DevicePath;

    public static IReadOnlyList<HidDevice> GetMatchingDevices() =>
        DeviceList.Local.GetHidDevices(DeviceConstants.VendorId, DeviceConstants.ProductId).ToList();

    public static TemperHumReader Open()
    {
        var devices = GetMatchingDevices();
        if (devices.Count == 0)
        {
            throw new InvalidOperationException(
                $"No TEMPerHUM-compatible HID device found (VID {DeviceConstants.VendorId:X4}, PID {DeviceConstants.ProductId:X4}).");
        }

        var ordered = devices
            .OrderByDescending(d => d.DevicePath.Contains("mi_01", StringComparison.OrdinalIgnoreCase))
#pragma warning disable CS0612
            .ThenByDescending(d => d.MaxInputReportLength >= 8 && d.MaxOutputReportLength >= 8)
#pragma warning restore CS0612
            .ToList();

        foreach (var device in ordered)
        {
            if (device.TryOpen(out var stream))
            {
                return new TemperHumReader(device, stream);
            }
        }

        throw new InvalidOperationException(
            "The matching HID interfaces were found, but none could be opened. " +
            "Close the vendor TEMPerHUM application and try again.");
    }

    public Measurement ReadMeasurement()
    {
#pragma warning disable CS0612
        var outputLength = Math.Max(_device.MaxOutputReportLength, MeasurementCommand.Length + 1);
#pragma warning restore CS0612

        var command = new byte[outputLength];
        command[0] = 0x00;
        Array.Copy(MeasurementCommand, 0, command, 1, MeasurementCommand.Length);
        _stream.Write(command);

        var packets = new List<byte>();
        var deadline = DateTime.UtcNow.AddMilliseconds(650);

        while (DateTime.UtcNow < deadline && packets.Count < 16)
        {
            try
            {
#pragma warning disable CS0612
                var buffer = new byte[Math.Max(_device.MaxInputReportLength, 9)];
#pragma warning restore CS0612
                var read = _stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    continue;
                }

                var payload = StripReportId(buffer.AsSpan(0, read));
                packets.AddRange(payload.ToArray());

                if (packets.Count >= 6)
                {
                    break;
                }
            }
            catch (TimeoutException)
            {
                break;
            }
        }

        if (packets.Count < 6)
        {
            throw new InvalidOperationException(
                $"Sensor returned only {packets.Count} payload byte(s); expected at least 6.");
        }

        var data = packets.ToArray();
        var temperature = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(2, 2)) / 100.0;
        var humidity = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(4, 2)) / 100.0;

        if (temperature is < -80 or > 100)
        {
            throw new InvalidOperationException(
                $"Implausible temperature decoded: {temperature:F2} °C. Raw: {Convert.ToHexString(data)}");
        }

        if (humidity is < 0 or > 100)
        {
            throw new InvalidOperationException(
                $"Implausible humidity decoded: {humidity:F2} %RH. Raw: {Convert.ToHexString(data)}");
        }

        return new Measurement(temperature, humidity);
    }

    private static ReadOnlySpan<byte> StripReportId(ReadOnlySpan<byte> report) =>
        report.Length == 9 ? report[1..] : report;

    public void Dispose() => _stream.Dispose();
}

internal static class DewPoint
{
    public static double Calculate(double temperatureC, double humidityPercent)
    {
        if (humidityPercent <= 0)
        {
            return double.NegativeInfinity;
        }

        const double a = 17.62;
        const double b = 243.12;
        var gamma = Math.Log(humidityPercent / 100.0) + (a * temperatureC) / (b + temperatureC);
        return (b * gamma) / (a - gamma);
    }
}
