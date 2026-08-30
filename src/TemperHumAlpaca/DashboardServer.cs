using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class DashboardServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task RunAsync(SensorService sensor, AppConfig config, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{config.DashboardPort}");

        var app = builder.Build();
        var advisor = new DewAdvisor();
        var trackingTask = advisor.TrackAsync(sensor, cancellationToken);

        app.MapGet("/", () => Results.Redirect("/dashboard"));
        app.MapGet("/dashboard", (HttpRequest request) =>
            Results.Content(BuildPage(sensor, config, advisor, request.Query["message"].ToString()), "text/html; charset=utf-8"));

        // Local machine-readable endpoint intended for lightweight integrations such
        // as a future N.I.N.A. plugin. It deliberately lives on the loopback-only
        // dashboard listener rather than extending the standard Alpaca contract.
        app.MapGet("/api/v1/status", () => BuildStatus(sensor, advisor));

        app.MapPost("/calibrate", async (HttpRequest request) =>
        {
            try
            {
                if (!sensor.Connected)
                {
                    throw new InvalidOperationException("The TEMPerHUM sensor must be connected before calibrating.");
                }

                var form = await request.ReadFormAsync(cancellationToken);
                var referenceTemperature = ParseDouble(form["referenceTemperature"], "Reference temperature");
                var referenceHumidity = ParseDouble(form["referenceHumidity"], "Reference humidity");

                if (referenceTemperature is < -50 or > 70)
                {
                    throw new InvalidOperationException("Reference temperature is outside the supported calibration range (-50 to 70 °C).");
                }

                if (referenceHumidity is < 0 or > 100)
                {
                    throw new InvalidOperationException("Reference humidity must be between 0 and 100 %RH.");
                }

                var snapshot = sensor.Snapshot;
                var rawTemperature = snapshot.TemperatureC - config.TemperatureOffsetC;
                var rawHumidity = snapshot.HumidityPercent - config.HumidityOffsetPercent;

                var newTemperatureOffset = referenceTemperature - rawTemperature;
                var newHumidityOffset = referenceHumidity - rawHumidity;
                ValidateOffsets(newTemperatureOffset, newHumidityOffset);

                config.TemperatureOffsetC = newTemperatureOffset;
                config.HumidityOffsetPercent = newHumidityOffset;
                SaveConfig(config);

                await sensor.RefreshAsync(cancellationToken);

                return Redirect(
                    $"Calibration saved: temperature {newTemperatureOffset:+0.00;-0.00;0.00} °C, humidity {newHumidityOffset:+0.00;-0.00;0.00} %RH.");
            }
            catch (Exception ex)
            {
                return Redirect($"Calibration failed: {ex.Message}");
            }
        });

        app.MapPost("/offsets", async (HttpRequest request) =>
        {
            try
            {
                var form = await request.ReadFormAsync(cancellationToken);
                var temperatureOffset = ParseDouble(form["temperatureOffset"], "Temperature offset");
                var humidityOffset = ParseDouble(form["humidityOffset"], "Humidity offset");
                ValidateOffsets(temperatureOffset, humidityOffset);

                config.TemperatureOffsetC = temperatureOffset;
                config.HumidityOffsetPercent = humidityOffset;
                SaveConfig(config);

                if (sensor.Connected)
                {
                    await sensor.RefreshAsync(cancellationToken);
                }

                return Redirect("Calibration offsets saved.");
            }
            catch (Exception ex)
            {
                return Redirect($"Could not save offsets: {ex.Message}");
            }
        });

        app.MapPost("/refresh", async () =>
        {
            try
            {
                if (!sensor.Connected)
                {
                    throw new InvalidOperationException("Sensor is disconnected.");
                }

                await sensor.RefreshAsync(cancellationToken);
                return Redirect("Sensor reading refreshed.");
            }
            catch (Exception ex)
            {
                return Redirect($"Refresh failed: {ex.Message}");
            }
        });

        app.MapPost("/reconnect", async () =>
        {
            try
            {
                await sensor.DisconnectNowAsync();
                await sensor.ConnectNowAsync(cancellationToken);
                return Redirect("Sensor reconnected.");
            }
            catch (Exception ex)
            {
                return Redirect($"Reconnect failed: {ex.Message}");
            }
        });

        try
        {
            await app.RunAsync(cancellationToken);
        }
        finally
        {
            try
            {
                await trackingTask;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private static IResult BuildStatus(SensorService sensor, DewAdvisor advisor)
    {
        if (!sensor.Connected)
        {
            return Results.Json(new
            {
                version = AppInfo.Version,
                connected = false,
                connecting = sensor.Connecting,
                lastError = sensor.LastError
            });
        }

        try
        {
            var snapshot = sensor.Snapshot;
            var advice = advisor.Evaluate(snapshot);
            return Results.Json(new
            {
                version = AppInfo.Version,
                connected = true,
                updatedAt = snapshot.UpdatedAt,
                temperatureC = snapshot.TemperatureC,
                humidityPercent = snapshot.HumidityPercent,
                dewPointC = snapshot.DewPointC,
                dewMarginC = advice.DewMarginC,
                dewRisk = advice.Risk,
                recommendedHeaterPowerPercent = advice.RecommendedPowerPercent,
                astroZapKnobPosition = advice.KnobPosition,
                dewMarginTrend = advice.Trend,
                dewMarginTrendCPerHour = advice.DewMarginTrendCPerHour,
                advisory = advice.Note
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new
            {
                version = AppInfo.Version,
                connected = sensor.Connected,
                error = ex.Message
            });
        }
    }

    private static IResult Redirect(string message) =>
        Results.Redirect($"/dashboard?message={WebUtility.UrlEncode(message)}");

    private static double ParseDouble(string raw, string fieldName)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException($"{fieldName} must be a number using a decimal point.");
        }

        return value;
    }

    private static void ValidateOffsets(double temperatureOffset, double humidityOffset)
    {
        if (temperatureOffset is < -15 or > 15)
        {
            throw new InvalidOperationException("Temperature offset must be between -15 and +15 °C.");
        }

        if (humidityOffset is < -30 or > 30)
        {
            throw new InvalidOperationException("Humidity offset must be between -30 and +30 %RH.");
        }
    }

    private static void SaveConfig(AppConfig config)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "temperhum.json");
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    private static string BuildPage(SensorService sensor, AppConfig config, DewAdvisor advisor, string message)
    {
        string status;
        string temperature = "—";
        string humidity = "—";
        string dewPoint = "—";
        string dewMargin = "—";
        string dewRisk = "—";
        string heaterPower = "—";
        string knobPosition = "—";
        string trend = "—";
        string trendRate = string.Empty;
        string rawTemperature = "—";
        string rawHumidity = "—";
        string age = "—";

        if (sensor.Connected)
        {
            try
            {
                var snapshot = sensor.Snapshot;
                var advice = advisor.Evaluate(snapshot);
                var rawTempValue = snapshot.TemperatureC - config.TemperatureOffsetC;
                var rawHumidityValue = snapshot.HumidityPercent - config.HumidityOffsetPercent;
                var ageSeconds = Math.Max(0, (DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalSeconds);

                status = "Connected";
                temperature = $"{snapshot.TemperatureC:F2} °C";
                humidity = $"{snapshot.HumidityPercent:F2} %";
                dewPoint = $"{snapshot.DewPointC:F2} °C";
                dewMargin = $"{advice.DewMarginC:F2} °C";
                dewRisk = advice.Risk;
                heaterPower = $"~{advice.RecommendedPowerPercent}%";
                knobPosition = advice.KnobPosition;
                trend = advice.Trend;
                trendRate = advice.DewMarginTrendCPerHour is double rate
                    ? $" ({rate:+0.00;-0.00;0.00} °C/hr)"
                    : string.Empty;
                rawTemperature = $"{rawTempValue:F2} °C";
                rawHumidity = $"{rawHumidityValue:F2} %";
                age = ageSeconds < 60 ? $"{ageSeconds:F1} s" : $"{ageSeconds / 60.0:F1} min";
            }
            catch (Exception ex)
            {
                status = $"Reading error: {WebUtility.HtmlEncode(ex.Message)}";
            }
        }
        else if (sensor.Connecting)
        {
            status = "Connecting";
        }
        else
        {
            status = "Disconnected";
        }

        var lastError = string.IsNullOrWhiteSpace(sensor.LastError)
            ? "None"
            : WebUtility.HtmlEncode(sensor.LastError);

        var notice = string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : $"<div class=\"notice\">{WebUtility.HtmlEncode(message)}</div>";

        return $$"""
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1">
          <meta http-equiv="refresh" content="15">
          <title>TemperHumAlpaca Dashboard</title>
          <style>
            :root{color-scheme:light dark;--border:#8a8a8a55;--panel:#8881;--good:#2e9b55;--warn:#d18a00}
            body{font-family:Segoe UI,Arial,sans-serif;max-width:1060px;margin:32px auto;padding:0 20px;line-height:1.45}
            h1{margin-bottom:4px}.sub{opacity:.7;margin-top:0}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px;margin:22px 0}
            .card,.panel{border:1px solid var(--border);border-radius:12px;background:var(--panel);padding:16px}.label{font-size:.86rem;opacity:.7}.value{font-size:1.8rem;font-weight:650;margin-top:4px}.detail{font-size:.85rem;opacity:.75;margin-top:4px}
            .panel{margin:14px 0}.row{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px}label{display:block;font-size:.88rem;margin-bottom:4px}
            input{box-sizing:border-box;width:100%;padding:9px;border:1px solid var(--border);border-radius:7px}button{padding:9px 14px;border-radius:7px;border:1px solid var(--border);cursor:pointer;margin-top:10px}
            code{background:#8882;padding:2px 5px;border-radius:4px}.notice{border-left:4px solid var(--good);padding:10px 12px;background:#2e9b5518;margin:16px 0;border-radius:6px}
            .small{font-size:.88rem;opacity:.75}.status{font-weight:650}.advisory{border-left:4px solid var(--warn)}
          </style>
        </head>
        <body>
          <h1>TemperHumAlpaca</h1>
          <p class="sub">v{{AppInfo.Version}} · local observatory environment dashboard</p>
          {{notice}}

          <div class="grid">
            <div class="card"><div class="label">Temperature</div><div class="value">{{temperature}}</div></div>
            <div class="card"><div class="label">Humidity</div><div class="value">{{humidity}}</div></div>
            <div class="card"><div class="label">Dew point</div><div class="value">{{dewPoint}}</div></div>
            <div class="card"><div class="label">Dew margin</div><div class="value">{{dewMargin}}</div><div class="detail">{{trend}}{{trendRate}}</div></div>
          </div>

          <div class="grid">
            <div class="card"><div class="label">Dew risk</div><div class="value">{{dewRisk}}</div></div>
            <div class="card"><div class="label">AstroZap estimate</div><div class="value">{{heaterPower}}</div><div class="detail">Knob: {{knobPosition}}</div></div>
          </div>

          <div class="panel advisory">
            <strong>Heater guidance is advisory.</strong>
            <div class="small">The estimate maps dew margin to the AstroZap dual-channel controller's approximate 5–95% duty-cycle range and adjusts modestly when the margin is trending down. Because TemperHumAlpaca measures ambient air rather than the objective itself, use this as a starting knob position, not a guarantee against dew.</div>
          </div>

          <div class="panel">
            <strong>Status:</strong> <span class="status">{{status}}</span><br>
            <span class="small">Reading age: {{age}} · Raw sensor: {{rawTemperature}}, {{rawHumidity}} RH · Last error: {{lastError}}</span>
            <form method="post" action="/refresh" style="display:inline"><button type="submit">Refresh reading</button></form>
            <form method="post" action="/reconnect" style="display:inline"><button type="submit">Reconnect USB sensor</button></form>
          </div>

          <div class="panel">
            <h2>Calibrate against reference thermometer</h2>
            <p class="small">Place the reference sensor beside the TEMPerHUM, allow both to stabilise, then enter the reference reading. The app calculates and saves new offsets from the current raw USB reading.</p>
            <form method="post" action="/calibrate">
              <div class="row">
                <div><label for="referenceTemperature">Reference temperature (°C)</label><input id="referenceTemperature" name="referenceTemperature" type="number" step="0.01" required></div>
                <div><label for="referenceHumidity">Reference humidity (%RH)</label><input id="referenceHumidity" name="referenceHumidity" type="number" step="0.01" min="0" max="100" required></div>
              </div>
              <button type="submit">Calculate and save calibration</button>
            </form>
          </div>

          <div class="panel">
            <h2>Manual calibration offsets</h2>
            <form method="post" action="/offsets">
              <div class="row">
                <div><label for="temperatureOffset">Temperature offset (°C)</label><input id="temperatureOffset" name="temperatureOffset" type="number" step="0.01" value="{{config.TemperatureOffsetC.ToString("F2", CultureInfo.InvariantCulture)}}" required></div>
                <div><label for="humidityOffset">Humidity offset (%RH)</label><input id="humidityOffset" name="humidityOffset" type="number" step="0.01" value="{{config.HumidityOffsetPercent.ToString("F2", CultureInfo.InvariantCulture)}}" required></div>
              </div>
              <button type="submit">Save offsets</button>
            </form>
          </div>

          <div class="panel small">
            <strong>Alpaca:</strong> HTTP {{config.AlpacaPort}}, discovery UDP {{config.DiscoveryPort}}, ObservingConditions 0<br>
            <strong>Dashboard:</strong> localhost:{{config.DashboardPort}} · <strong>status API:</strong> <code>/api/v1/status</code><br>
            <strong>Unique ID:</strong> <code>{{WebUtility.HtmlEncode(config.UniqueId)}}</code><br>
            Configuration: <code>{{WebUtility.HtmlEncode(Path.Combine(AppContext.BaseDirectory, "temperhum.json"))}}</code>
          </div>
        </body>
        </html>
        """;
    }
}
