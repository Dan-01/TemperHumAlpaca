using System.Globalization;
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

        if (options.Probe)
        {
            return DeviceProbe.Run(options.ProbeAll, options.VendorId, options.ProductId);
        }

        var config = LoadConfig();
        if (!string.IsNullOrWhiteSpace(options.DeviceProfile))
        {
            config.DeviceProfile = options.DeviceProfile;
        }

        ValidateConfiguredProfile(config.DeviceProfile);

        if (options.List)
        {
            ListDevices(config.DeviceProfile);
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

    private static void ValidateConfiguredProfile(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) ||
            profileId.Equals(DeviceProfiles.Auto, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = DeviceProfiles.Resolve(profileId);
    }

    private static int RunOnce(AppConfig config)
    {
        Console.WriteLine($"TemperHumAlpaca v{AppInfo.Version} USB test");
        Console.WriteLine($"Device profile: {config.DeviceProfile}");
        Console.WriteLine("Close the vendor TEMPerHUM application before running this tool.\n");

        try
        {
            using var reader = TemperHumReader.Open(config.DeviceProfile);
            Console.WriteLine($"Profile: {reader.Profile.Id}");
            Console.WriteLine($"Opened HID interface: {reader.DevicePath}\n");
            PrintMeasurement(reader.ReadMeasurement(), config);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine("Run with --probe to inspect likely TEMPer-family HID interfaces.");
            return 1;
        }
    }

    private static async Task<int> RunMonitorAsync(AppConfig config)
    {
        Console.WriteLine($"TemperHumAlpaca v{AppInfo.Version} USB monitor");
        Console.WriteLine($"Device profile: {config.DeviceProfile}");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        using var cts = CreateConsoleCancellation();
        try
        {
            using var reader = TemperHumReader.Open(config.DeviceProfile);
            Console.WriteLine($"Profile: {reader.Profile.Id}");
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

        if (string.IsNullOrWhiteSpace(config.DeviceProfile))
        {
            config.DeviceProfile = DeviceProfiles.Auto;
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

    private static void ListDevices(string? profileId)
    {
        var devices = TemperHumReader.GetMatchingDevices(profileId);
        if (devices.Count == 0)
        {
            Console.WriteLine("No HID interfaces matched the configured supported device profile.");
            Console.WriteLine("Run --probe to inspect likely TEMPer-family devices or --probe-all for all HID devices.");
            return;
        }

        Console.WriteLine($"Found {devices.Count} matching HID interface(s):\n");
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            Console.WriteLine($"[{i}] VID:PID {device.VendorID:X4}:{device.ProductID:X4}");
            Console.WriteLine($"    support={DeviceProfiles.SupportLabel(device)}");
            Console.WriteLine($"    path={device.DevicePath}");
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
    public string DeviceProfile { get; set; } = DeviceProfiles.Auto;
    public string UniqueId { get; set; } = string.Empty;
}

internal sealed class Arguments
{
    public bool Once { get; init; }
    public bool List { get; init; }
    public bool Monitor { get; init; }
    public bool Probe { get; init; }
    public bool ProbeAll { get; init; }
    public bool Service { get; init; }
    public bool InstallService { get; init; }
    public bool UninstallService { get; init; }
    public bool ServiceStatus { get; init; }
    public int? VendorId { get; init; }
    public int? ProductId { get; init; }
    public string? DeviceProfile { get; init; }

    public static Arguments Parse(string[] args)
    {
        var probeAll = Has(args, "--probe-all");
        return new Arguments
        {
            Once = Has(args, "--once"),
            List = Has(args, "--list"),
            Monitor = Has(args, "--monitor"),
            Probe = Has(args, "--probe") || probeAll,
            ProbeAll = probeAll,
            Service = Has(args, "--service"),
            InstallService = Has(args, "--install-service"),
            UninstallService = Has(args, "--uninstall-service"),
            ServiceStatus = Has(args, "--service-status"),
            VendorId = ParseHexOption(args, "--vid"),
            ProductId = ParseHexOption(args, "--pid"),
            DeviceProfile = GetOption(args, "--profile")
        };
    }

    private static bool Has(string[] args, string value) =>
        args.Any(a => a.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static string? GetOption(string[] args, string option)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{option} requires a value.");
            }

            return args[i + 1];
        }

        return null;
    }

    private static int? ParseHexOption(string[] args, string option)
    {
        var raw = GetOption(args, option);
        if (raw is null)
        {
            return null;
        }

        raw = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
        if (!int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 0xFFFF)
        {
            throw new ArgumentException($"{option} must be a 16-bit hexadecimal value such as 413D or 0x413D.");
        }

        return value;
    }
}
