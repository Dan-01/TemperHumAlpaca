using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class AlpacaErrors
{
    public const int NoError = 0;
    public const int NotImplemented = 1024; // 0x400
    public const int InvalidValue = 1025;   // 0x401
    public const int NotConnected = 1031;   // 0x407
    public const int ActionNotImplemented = 1036; // 0x40C
    public const int DriverError = 1280;    // 0x500, driver-specific range
}

internal sealed class AlpacaDeviceException : Exception
{
    public AlpacaDeviceException(int errorNumber, string message) : base(message)
    {
        ErrorNumber = errorNumber;
    }

    public int ErrorNumber { get; }
}

internal static class AlpacaServer
{
    private static int _serverTransactionId;

    public static async Task RunAsync(SensorService sensor, AppConfig config, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{config.AlpacaPort}");

        var app = builder.Build();

        app.MapGet("/", () => Results.Redirect("/setup"));
        app.MapGet("/setup", () => Results.Content(BuildSetupPage(sensor, config), "text/html; charset=utf-8"));
        app.MapGet("/setup/v1/observingconditions/0/setup", () =>
            Results.Content(BuildSetupPage(sensor, config), "text/html; charset=utf-8"));

        app.MapGet("/management/apiversions", (HttpRequest request) =>
            Success(request, new[] { 1 }));

        app.MapGet("/management/v1/description", (HttpRequest request) =>
            Success(request, new
            {
                ServerName = "TemperHumAlpaca",
                Manufacturer = "TemperHumAlpaca open-source project",
                ManufacturerVersion = AppInfo.Version,
                Location = Environment.MachineName
            }));

        app.MapGet("/management/v1/configureddevices", (HttpRequest request) =>
            Success(request, new[]
            {
                new
                {
                    DeviceName = "TEMPerHUM Observing Conditions",
                    DeviceType = "ObservingConditions",
                    DeviceNumber = 0,
                    UniqueID = config.UniqueId
                }
            }));

        var device = app.MapGroup("/api/v1/observingconditions/0");

        device.MapGet("/connected", (HttpRequest request) =>
            HandleGet(request, () => sensor.Connected));

        device.MapPut("/connected", async (HttpRequest request) =>
            await HandlePutAsync(request, async () =>
            {
                var form = await request.ReadFormAsync(cancellationToken);
                var raw = GetFormValue(form, "Connected") ??
                          throw new AlpacaDeviceException(AlpacaErrors.InvalidValue, "Connected parameter is required.");

                if (!bool.TryParse(raw, out var connected))
                {
                    throw new AlpacaDeviceException(AlpacaErrors.InvalidValue, $"Connected value '{raw}' is invalid.");
                }

                if (connected)
                {
                    await sensor.ConnectNowAsync(cancellationToken);
                }
                else
                {
                    await sensor.DisconnectNowAsync();
                }
            }));

        device.MapPut("/connect", (HttpRequest request) =>
            HandlePut(request, sensor.BeginConnect));

        device.MapPut("/disconnect", (HttpRequest request) =>
            HandlePut(request, sensor.BeginDisconnect));

        device.MapGet("/connecting", (HttpRequest request) =>
            HandleGet(request, () => sensor.Connecting));

        device.MapGet("/description", (HttpRequest request) =>
            HandleGet(request, () => "PCsensor TEMPerHUM USB temperature/humidity sensor"));

        device.MapGet("/driverinfo", (HttpRequest request) =>
            HandleGet(request, () => $"TemperHumAlpaca v{AppInfo.Version} - TEMPerHUM to ASCOM Alpaca ObservingConditions"));

        device.MapGet("/driverversion", (HttpRequest request) =>
            HandleGet(request, () => AppInfo.Version));

        device.MapGet("/interfaceversion", (HttpRequest request) =>
            HandleGet(request, () => 2));

        device.MapGet("/name", (HttpRequest request) =>
            HandleGet(request, () => "TemperHumAlpaca"));

        device.MapGet("/supportedactions", (HttpRequest request) =>
            HandleGet(request, () => Array.Empty<string>()));

        device.MapGet("/averageperiod", (HttpRequest request) =>
            HandleGet(request, () => 0.0));

        device.MapPut("/averageperiod", async (HttpRequest request) =>
            await HandlePutAsync(request, async () =>
            {
                var form = await request.ReadFormAsync(cancellationToken);
                var raw = GetFormValue(form, "AveragePeriod") ??
                          throw new AlpacaDeviceException(AlpacaErrors.InvalidValue, "AveragePeriod parameter is required.");

                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value != 0.0)
                {
                    throw new AlpacaDeviceException(
                        AlpacaErrors.InvalidValue,
                        "This device provides instantaneous readings; AveragePeriod must be 0.0 hours.");
                }
            }));

        device.MapGet("/temperature", (HttpRequest request) =>
            HandleGet(request, () => sensor.Snapshot.TemperatureC));

        device.MapGet("/humidity", (HttpRequest request) =>
            HandleGet(request, () => sensor.Snapshot.HumidityPercent));

        device.MapGet("/dewpoint", (HttpRequest request) =>
            HandleGet(request, () => sensor.Snapshot.DewPointC));

        device.MapGet("/devicestate", (HttpRequest request) =>
            HandleGet(request, () =>
            {
                var snapshot = sensor.Snapshot;
                return new object[]
                {
                    new { Name = "DewPoint", Value = (object)snapshot.DewPointC },
                    new { Name = "Humidity", Value = (object)snapshot.HumidityPercent },
                    new { Name = "Temperature", Value = (object)snapshot.TemperatureC },
                    new { Name = "TimeStamp", Value = (object)snapshot.UpdatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) }
                };
            }));

        device.MapPut("/refresh", async (HttpRequest request) =>
            await HandlePutAsync(request, () => sensor.RefreshAsync(cancellationToken)));

        device.MapGet("/sensordescription", (HttpRequest request) =>
            HandleGet(request, () =>
            {
                var sensorName = GetQueryValue(request, "SensorName") ??
                                 throw new AlpacaDeviceException(AlpacaErrors.InvalidValue, "SensorName parameter is required.");
                return sensor.SensorDescription(sensorName);
            }));

        device.MapGet("/timesincelastupdate", (HttpRequest request) =>
            HandleGet(request, () =>
            {
                var sensorName = GetQueryValue(request, "SensorName") ?? string.Empty;
                return sensor.TimeSinceLastUpdate(sensorName);
            }));

        foreach (var property in new[]
                 {
                     "cloudcover", "pressure", "rainrate", "skybrightness", "skyquality",
                     "skytemperature", "starfwhm", "winddirection", "windgust", "windspeed"
                 })
        {
            var propertyName = property;
            device.MapGet($"/{propertyName}", (HttpRequest request) =>
                HandleGet(request, () => throw new AlpacaDeviceException(
                    AlpacaErrors.NotImplemented,
                    $"ObservingConditions.{propertyName} is not implemented by this TEMPerHUM sensor.")));
        }

        device.MapPut("/action", (HttpRequest request) =>
            Error(request, AlpacaErrors.ActionNotImplemented, "No custom actions are implemented."));
        device.MapPut("/commandblind", (HttpRequest request) =>
            Error(request, AlpacaErrors.NotImplemented, "CommandBlind is not implemented."));
        device.MapPut("/commandbool", (HttpRequest request) =>
            Error(request, AlpacaErrors.NotImplemented, "CommandBool is not implemented."));
        device.MapPut("/commandstring", (HttpRequest request) =>
            Error(request, AlpacaErrors.NotImplemented, "CommandString is not implemented."));

        await using var discovery = new AlpacaDiscoveryResponder(config.AlpacaPort, config.DiscoveryPort, config.DiscoveryEnabled);
        discovery.Start();

        Console.WriteLine($"TemperHumAlpaca v{AppInfo.Version}");
        Console.WriteLine($"Alpaca HTTP:       http://localhost:{config.AlpacaPort}/setup");
        Console.WriteLine($"ObservingConditions device: 0 (InterfaceVersion 2)");
        Console.WriteLine(config.DiscoveryEnabled
            ? $"Alpaca discovery:  UDP {config.DiscoveryPort}"
            : "Alpaca discovery:  disabled");
        Console.WriteLine($"Sensor connected:  {sensor.Connected}");
        if (!string.IsNullOrWhiteSpace(sensor.LastError))
        {
            Console.WriteLine($"Sensor warning:    {sensor.LastError}");
        }
        Console.WriteLine("Press Ctrl+C to stop.\n");

        await app.RunAsync(cancellationToken);
    }

    private static IResult HandleGet(HttpRequest request, Func<object?> operation)
    {
        try
        {
            return Success(request, operation());
        }
        catch (AlpacaDeviceException ex)
        {
            return Error(request, ex.ErrorNumber, ex.Message);
        }
        catch (Exception ex)
        {
            return Error(request, AlpacaErrors.DriverError, ex.Message);
        }
    }

    private static IResult HandlePut(HttpRequest request, Action operation)
    {
        try
        {
            operation();
            return SuccessNoValue(request);
        }
        catch (AlpacaDeviceException ex)
        {
            return Error(request, ex.ErrorNumber, ex.Message);
        }
        catch (Exception ex)
        {
            return Error(request, AlpacaErrors.DriverError, ex.Message);
        }
    }

    private static async Task<IResult> HandlePutAsync(HttpRequest request, Func<Task> operation)
    {
        try
        {
            await operation();
            return await SuccessNoValueAsync(request);
        }
        catch (AlpacaDeviceException ex)
        {
            return await ErrorAsync(request, ex.ErrorNumber, ex.Message);
        }
        catch (Exception ex)
        {
            return await ErrorAsync(request, AlpacaErrors.DriverError, ex.Message);
        }
    }

    private static IResult Success(HttpRequest request, object? value)
    {
        return Results.Json(new
        {
            Value = value,
            ClientTransactionID = GetClientTransactionId(request),
            ServerTransactionID = NextServerTransactionId(),
            ErrorNumber = 0,
            ErrorMessage = string.Empty
        });
    }

    private static IResult SuccessNoValue(HttpRequest request)
    {
        return Results.Json(new
        {
            ClientTransactionID = GetClientTransactionId(request),
            ServerTransactionID = NextServerTransactionId(),
            ErrorNumber = 0,
            ErrorMessage = string.Empty
        });
    }

    private static async Task<IResult> SuccessNoValueAsync(HttpRequest request)
    {
        return Results.Json(new
        {
            ClientTransactionID = await GetClientTransactionIdAsync(request),
            ServerTransactionID = NextServerTransactionId(),
            ErrorNumber = 0,
            ErrorMessage = string.Empty
        });
    }

    private static IResult Error(HttpRequest request, int errorNumber, string message)
    {
        return Results.Json(new
        {
            ClientTransactionID = GetClientTransactionId(request),
            ServerTransactionID = NextServerTransactionId(),
            ErrorNumber = errorNumber,
            ErrorMessage = message
        });
    }

    private static async Task<IResult> ErrorAsync(HttpRequest request, int errorNumber, string message)
    {
        return Results.Json(new
        {
            ClientTransactionID = await GetClientTransactionIdAsync(request),
            ServerTransactionID = NextServerTransactionId(),
            ErrorNumber = errorNumber,
            ErrorMessage = message
        });
    }

    private static int GetClientTransactionId(HttpRequest request)
    {
        var raw = GetQueryValue(request, "ClientTransactionID");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }

    private static async Task<int> GetClientTransactionIdAsync(HttpRequest request)
    {
        var query = GetQueryValue(request, "ClientTransactionID");
        if (int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out var queryId))
        {
            return queryId;
        }

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            var raw = GetFormValue(form, "ClientTransactionID");
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var formId))
            {
                return formId;
            }
        }

        return 0;
    }

    private static string? GetQueryValue(HttpRequest request, string name)
    {
        foreach (var pair in request.Query)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value.ToString();
            }
        }

        return null;
    }

    private static string? GetFormValue(IFormCollection form, string name)
    {
        foreach (var pair in form)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value.ToString();
            }
        }

        return null;
    }

    private static int NextServerTransactionId()
    {
        var value = Interlocked.Increment(ref _serverTransactionId);
        if (value == int.MaxValue)
        {
            Interlocked.Exchange(ref _serverTransactionId, 0);
        }
        return value;
    }

    private static string BuildSetupPage(SensorService sensor, AppConfig config)
    {
        string measurement;
        if (sensor.Connected)
        {
            try
            {
                var s = sensor.Snapshot;
                measurement = $"{s.TemperatureC:F2} &deg;C &nbsp; {s.HumidityPercent:F2}% RH &nbsp; dew point {s.DewPointC:F2} &deg;C";
            }
            catch (Exception ex)
            {
                measurement = WebUtility.HtmlEncode(ex.Message);
            }
        }
        else
        {
            measurement = "Sensor disconnected";
        }

        var error = string.IsNullOrWhiteSpace(sensor.LastError)
            ? string.Empty
            : $"<p><strong>Last sensor error:</strong> {WebUtility.HtmlEncode(sensor.LastError)}</p>";

        return $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>TemperHumAlpaca</title>
        <style>body{font-family:Segoe UI,Arial,sans-serif;max-width:760px;margin:40px auto;padding:0 20px;line-height:1.5}code{background:#eee;padding:2px 5px}</style>
        </head><body>
        <h1>TemperHumAlpaca</h1>
        <p>ASCOM Alpaca ObservingConditions server for PCsensor TEMPerHUM/TEMPerX USB sensors.</p>
        <p><strong>Version:</strong> {{AppInfo.Version}}</p>
        <p><strong>Status:</strong> {{measurement}}</p>
        {{error}}
        <p><strong>Alpaca port:</strong> {{config.AlpacaPort}}<br>
        <strong>Discovery port:</strong> {{config.DiscoveryPort}}<br>
        <strong>Device:</strong> ObservingConditions 0<br>
        <strong>Unique ID:</strong> <code>{{WebUtility.HtmlEncode(config.UniqueId)}}</code></p>
        <p>Calibration and server settings are stored in <code>temperhum.json</code> beside the executable.</p>
        </body></html>
        """;
    }
}

internal sealed class AlpacaDiscoveryResponder : IAsyncDisposable
{
    private readonly int _alpacaPort;
    private readonly int _discoveryPort;
    private readonly bool _enabled;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public AlpacaDiscoveryResponder(int alpacaPort, int discoveryPort, bool enabled)
    {
        _alpacaPort = alpacaPort;
        _discoveryPort = discoveryPort;
        _enabled = enabled;
    }

    public void Start()
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => ListenAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: Alpaca discovery could not listen on UDP {_discoveryPort}: {ex.Message}");
            _udp?.Dispose();
            _udp = null;
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        if (_udp is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(cancellationToken);
                var request = Encoding.ASCII.GetString(result.Buffer);
                if (!request.StartsWith("alpacadiscovery1", StringComparison.Ordinal))
                {
                    continue;
                }

                var response = JsonSerializer.SerializeToUtf8Bytes(new { AlpacaPort = _alpacaPort });
                await _udp.SendAsync(response, response.Length, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WARNING: Alpaca discovery error: {ex.Message}");
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
        }
        _udp?.Dispose();

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts?.Dispose();
    }
}
