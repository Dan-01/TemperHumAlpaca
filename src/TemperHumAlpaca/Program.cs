using System.Buffers.Binary;
using System.Text.Json;
using HidSharp;

var app = new TemperHumApp();
return await app.RunAsync(args);

internal static class DeviceConstants
{
    public const int VendorId = 0x413D;
    public const int ProductId = 0x2107;
}

internal sealed class TemperHumApp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<int> RunAsync(string[] args)
    {
        var options = Arguments.Parse(args);
        var config = LoadConfig();

        if (options.List)
        {
            ListDevices();
            return 0;
        }

        Console.WriteLine("TemperHumAlpaca v0.1 USB reader");
        Console.WriteLine($"Target: VID {DeviceConstants.VendorId:X4} / PID {DeviceConstants.ProductId:X4}");
        Console.WriteLine("Close the vendor TEMPerHUM application before running this tool.\n");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        try
        {
            using var reader = TemperHumReader.Open();
            Console.WriteLine($"Opened HID interface: {reader.DevicePath}\n");

            do
            {
                var raw = reader.ReadMeasurement();
                var correctedTemperature = raw.TemperatureC + config.TemperatureOffsetC;
                var correctedHumidity = Math.Clamp(raw.HumidityPercent + config.HumidityOffsetPercent, 0.0, 100.0);
                var dewPoint = DewPoint.Calculate(correctedTemperature, correctedHumidity);

                Console.WriteLine(
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  " +
                    $"{correctedTemperature:F2} °C  " +
                    $"{correctedHumidity:F2} %RH  " +
                    $"dew {dewPoint:F2} °C");

                if (options.Once)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, config.PollIntervalSeconds)), cts.Token);
            }
            while (!cts.IsCancellationRequested);

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine("Run with --list to inspect all matching HID interfaces.");
            return 1;
        }
    }

    private static AppConfig LoadConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "temperhum.json");
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not read {path}: {ex.Message}", ex);
        }
    }

    private static void ListDevices()
    {
        var devices = DeviceList.Local.GetHidDevices(DeviceConstants.VendorId, DeviceConstants.ProductId).ToList();
        if (devices.Count == 0)
        {
            Console.WriteLine($"No HID devices found for VID {DeviceConstants.VendorId:X4} / PID {DeviceConstants.ProductId:X4}.");
            return;
        }

        Console.WriteLine($"Found {devices.Count} matching HID interface(s):\n");
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            Console.WriteLine($"[{i}] {device.DevicePath}");
#pragma warning disable CS0612
            Console.WriteLine($"    input={device.MaxInputReportLength}, output={device.MaxOutputReportLength}, feature={device.MaxFeatureReportLength}");
#pragma warning restore CS0612
        }
    }
}

internal sealed class TemperHumReader : IDisposable
{
    // Payload used by TEMPerX/TEMPerHUM firmware. On Linux hidraw this is written
    // as 8 bytes. HIDSharp on Windows requires an additional report-ID byte at
    // index 0, so ReadMeasurement prefixes this payload with report ID 0.
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

    public static TemperHumReader Open()
    {
        var devices = DeviceList.Local.GetHidDevices(DeviceConstants.VendorId, DeviceConstants.ProductId).ToList();
        if (devices.Count == 0)
        {
            throw new InvalidOperationException(
                $"No TEMPerHUM-compatible HID device found (VID {DeviceConstants.VendorId:X4}, PID {DeviceConstants.ProductId:X4}).");
        }

        // Windows exposes this composite device through multiple HID interfaces.
        // The known TEMPerX/TEMPerHUM protocol lives on MI_01, so prefer it.
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

        // HIDSharp's Write buffer includes the report-ID byte. This device uses
        // unnumbered reports, so byte 0 must be 0 and the 8-byte command starts
        // at byte 1. With this sensor MaxOutputReportLength is 9.
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
                $"Sensor returned only {packets.Count} payload byte(s); expected at least 6. " +
                "Use --list and report the interface lengths if this persists.");
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

    private static ReadOnlySpan<byte> StripReportId(ReadOnlySpan<byte> report)
    {
        // HidSharp includes the report-ID byte in each input report. MaxInputReportLength
        // is 9 for this device: one report-ID byte followed by the 8-byte sensor payload.
        return report.Length == 9 ? report[1..] : report;
    }

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

        // Magnus approximation, suitable for normal terrestrial observing conditions.
        const double a = 17.62;
        const double b = 243.12;
        var gamma = Math.Log(humidityPercent / 100.0) + (a * temperatureC) / (b + temperatureC);
        return (b * gamma) / (a - gamma);
    }
}

internal sealed record Measurement(double TemperatureC, double HumidityPercent);

internal sealed class AppConfig
{
    public double TemperatureOffsetC { get; set; }
    public double HumidityOffsetPercent { get; set; }
    public int PollIntervalSeconds { get; set; } = 1;
}

internal sealed class Arguments
{
    public bool Once { get; init; }
    public bool List { get; init; }

    public static Arguments Parse(string[] args) => new()
    {
        Once = args.Any(a => a.Equals("--once", StringComparison.OrdinalIgnoreCase)),
        List = args.Any(a => a.Equals("--list", StringComparison.OrdinalIgnoreCase))
    };
}
