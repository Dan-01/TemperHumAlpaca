using System.Reflection;
using System.Text.Json;

var app = new TemperHumApp();
return await app.RunAsync(args);

internal static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}

internal sealed class TemperHumApp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<int> RunAsync(string[] args)
    {
        var options = Arguments.Parse(args);

        if (options.InstallService)
        {
            return await WindowsServiceSupport.InstallAsync();
        }

        if (options.UninstallService)
        {
            return await WindowsServiceSupport.UninstallAsync();
        }

        if (options.ServiceStatus)
        {
            return WindowsServiceSupport.PrintStatus();
        }

        var config = LoadConfig();

        if (options.List)
        {
            ListDevices();
            return 0;
        }

        if (options.Once)
        {
            return RunOnce(config);
        }

        if (options.Monitor)
        {
            return await RunMonitorAsync(config);
        }

        if (options.Service)
        {
            return WindowsServiceSupport.Run(cancellationToken => RunServerAsync(config, cancellationToken));
        }

        using var cts = CreateConsoleCancellation();
        return await RunServerAsync(config, cts.Token);
    }

    private static int RunOnce(AppConfig config)
    {
        Console.WriteLine($"TemperHumAlpaca v{AppInfo.Version} USB test");
        Console.WriteLine($"Target: VID {DeviceConstants.VendorId:X4} / PID {DeviceConstants.ProductId:X4}");
        Console.WriteLine("Close the vendor TEMPerHUM application before running this tool.\n");

        try
        {
            using var reader = TemperHumReader.Open();
            Console.WriteLine($"Opened HID interface: {reader.DevicePath}\n");
            PrintMeasurement(reader.ReadMeasurement(), config);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine("Run with --list to inspect all matching HID interfaces.");
            return 1;
        }
    }

    private static async Task<int> RunMonitorAsync(AppConfig config)
    {
        Console.WriteLine($"TemperHumAlpaca v{AppInfo.Version} USB monitor");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        using var cts = CreateConsoleCancellation();
        try
        {
            using var reader = TemperHumReader.Open();
            Console.WriteLine($"Opened HID interface: {reader.DevicePath}\n");

            while (!cts.IsCancellationRequested)
            {
                PrintMeasurement(reader.ReadMeasurement(), config);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, config.PollIntervalSeconds)), cts.Token);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    internal static async Task<int> RunServerAsync(AppConfig config, CancellationToken cancellationToken)
    {
        await using var sensor = new SensorService(config);
        sensor.StartAutoReconnect();

        if (config.AutoConnect)
        {
            try
            {
                await sensor.ConnectNowAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // Keep the Alpaca server alive. The recovery loop will retry the USB
                // connection, which is important during Windows boot and USB re-plugs.
                Console.Error.WriteLine($"WARNING: Initial sensor connection failed: {ex.Message}");
            }
        }

        using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var dashboardTask = RunDashboardSafeAsync(sensor, config, serverCts.Token);

        try
        {
            await AlpacaServer.RunAsync(sensor, config, serverCts.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Alpaca server failed: {ex.Message}");
            return 1;
        }
        finally
        {
            serverCts.Cancel();
            await dashboardTask;
        }
    }

    private static async Task RunDashboardSafeAsync(SensorService sensor, AppConfig config, CancellationToken cancellationToken)
    {
        try
        {
            await DashboardServer.RunAsync(sensor, config, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Dashboard failure must never take the Alpaca weather device offline.
            Console.Error.WriteLine($"WARNING: Dashboard unavailable: {ex.Message}");
        }
    }

    private static AppConfig LoadConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "temperhum.json");
        AppConfig config;

        if (!File.Exists(path))
        {
            config = new AppConfig();
        }
        else
        {
            try
            {
                config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not read {path}: {ex.Message}", ex);
            }
        }

        if (string.IsNullOrWhiteSpace(config.UniqueId))
        {
            config.UniqueId = Guid.NewGuid().ToString("D");
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARNING: Could not persist generated Alpaca UniqueID to {path}: {ex.Message}");
            }
        }

        return config;
    }

    private static void ListDevices()
    {
        var devices = TemperHumReader.GetMatchingDevices();
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

    private static void PrintMeasurement(Measurement raw, AppConfig config)
    {
        var temperature = raw.TemperatureC + config.TemperatureOffsetC;
        var humidity = Math.Clamp(raw.HumidityPercent + config.HumidityOffsetPercent, 0.0, 100.0);
        var dewPoint = DewPoint.Calculate(temperature, humidity);

        Console.WriteLine(
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  " +
            $"{temperature:F2} °C  {humidity:F2} %RH  dew {dewPoint:F2} °C");
    }

    private static CancellationTokenSource CreateConsoleCancellation()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };
        return cts;
    }
}

internal sealed class AppConfig
{
    public double TemperatureOffsetC { get; set; }
    public double HumidityOffsetPercent { get; set; }
    public int PollIntervalSeconds { get; set; } = 1;
    public int ReconnectIntervalSeconds { get; set; } = 5;
    public int AlpacaPort { get; set; } = 11111;
    public int DashboardPort { get; set; } = 11112;
    public bool DiscoveryEnabled { get; set; } = true;
    public int DiscoveryPort { get; set; } = 32227;
    public bool AutoConnect { get; set; } = true;
    public string UniqueId { get; set; } = string.Empty;
}

internal sealed class Arguments
{
    public bool Once { get; init; }
    public bool List { get; init; }
    public bool Monitor { get; init; }
    public bool Service { get; init; }
    public bool InstallService { get; init; }
    public bool UninstallService { get; init; }
    public bool ServiceStatus { get; init; }

    public static Arguments Parse(string[] args) => new()
    {
        Once = Has(args, "--once"),
        List = Has(args, "--list"),
        Monitor = Has(args, "--monitor"),
        Service = Has(args, "--service"),
        InstallService = Has(args, "--install-service"),
        UninstallService = Has(args, "--uninstall-service"),
        ServiceStatus = Has(args, "--service-status")
    };

    private static bool Has(string[] args, string value) =>
        args.Any(a => a.Equals(value, StringComparison.OrdinalIgnoreCase));
}
