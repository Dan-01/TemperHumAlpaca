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
        using var forecast = new ForecastService();
        var trackingTask = advisor.TrackAsync(sensor, cancellationToken);
        var forecastTask = forecast.TrackAsync(sensor, config, cancellationToken);

        app.MapGet("/", () => Results.Redirect("/dashboard"));
        app.MapGet("/dashboard", (HttpRequest request) =>
            Results.Content(BuildPage(sensor, config, advisor, forecast, request.Query["message"].ToString()), "text/html; charset=utf-8"));

        // Local machine-readable endpoints intended for lightweight integrations such
        // as a future N.I.N.A. plugin. They deliberately live on the loopback-only
        // dashboard listener rather than extending the standard Alpaca contract.
        app.MapGet("/api/v1/status", () => BuildStatus(sensor, advisor, forecast));
        app.MapGet("/api/v1/history", () => Results.Json(new
        {
            version = AppInfo.Version,
            windowMinutes = 120,
            samples = advisor.GetHistory().Select(point => new
            {
                at = point.At,
                dewMarginC = point.DewMarginC
            })
        }));
        app.MapGet("/api/v1/forecast", () => Results.Json(forecast.Outlook));

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
                if (config.ForecastEnabled)
                {
                    await forecast.RefreshNowAsync(sensor, config, cancellationToken);
                }

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

                if (config.ForecastEnabled)
                {
                    await forecast.RefreshNowAsync(sensor, config, cancellationToken);
                }

                return Redirect("Calibration offsets saved.");
            }
            catch (Exception ex)
            {
                return Redirect($"Could not save offsets: {ex.Message}");
            }
        });

        app.MapPost("/forecast-settings", async (HttpRequest request) =>
        {
            try
            {
                var form = await request.ReadFormAsync(cancellationToken);
                var enabled = form.ContainsKey("forecastEnabled");
                var useEnsemble = form.ContainsKey("forecastUseEnsemble");
                var latitude = ParseNullableDouble(form["forecastLatitude"]);
                var longitude = ParseNullableDouble(form["forecastLongitude"]);
                var hours = ParseInt(form["forecastHours"], "Forecast horizon");
                var refreshMinutes = ParseInt(form["forecastRefreshMinutes"], "Forecast refresh interval");
                var safetyMargin = ParseDouble(form["forecastSafetyMarginC"], "Forecast safety margin");

                if (latitude is < -90 or > 90)
                {
                    throw new InvalidOperationException("Latitude must be between -90 and +90 degrees.");
                }

                if (longitude is < -180 or > 180)
                {
                    throw new InvalidOperationException("Longitude must be between -180 and +180 degrees.");
                }

                if (enabled && (latitude is null || longitude is null))
                {
                    throw new InvalidOperationException("Latitude and longitude are required when overnight forecasting is enabled.");
                }

                if (hours is < 6 or > 24)
                {
                    throw new InvalidOperationException("Forecast horizon must be between 6 and 24 hours.");
                }

                if (refreshMinutes is < 5 or > 180)
                {
                    throw new InvalidOperationException("Forecast refresh interval must be between 5 and 180 minutes.");
                }

                if (safetyMargin is < 0 or > 5)
                {
                    throw new InvalidOperationException("Forecast safety margin must be between 0 and 5 °C.");
                }

                config.ForecastEnabled = enabled;
                config.ForecastLatitude = latitude;
                config.ForecastLongitude = longitude;
                config.ForecastHours = hours;
                config.ForecastRefreshMinutes = refreshMinutes;
                config.ForecastSafetyMarginC = safetyMargin;
                config.ForecastUseEnsemble = useEnsemble;
                SaveConfig(config);

                forecast.ResetSessionRecommendation();
                await forecast.RefreshNowAsync(sensor, config, cancellationToken);
                return Redirect(enabled
                    ? "Overnight forecast settings saved and forecast refreshed."
                    : "Overnight forecasting disabled.");
            }
            catch (Exception ex)
            {
                return Redirect($"Could not save forecast settings: {ex.Message}");
            }
        });

        app.MapPost("/forecast-refresh", async () =>
        {
            try
            {
                await forecast.RefreshNowAsync(sensor, config, cancellationToken);
                return Redirect("Overnight forecast refreshed.");
            }
            catch (Exception ex)
            {
                return Redirect($"Forecast refresh failed: {ex.Message}");
            }
        });

        app.MapPost("/forecast-reset", () =>
        {
            forecast.ResetSessionRecommendation();
            return Redirect("Set-and-leave high-water mark reset to the current forecast recommendation.");
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
                await Task.WhenAll(trackingTask, forecastTask);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private static IResult BuildStatus(SensorService sensor, DewAdvisor advisor, ForecastService forecast)
    {
        var outlook = forecast.Outlook;
        if (!sensor.Connected)
        {
            return Results.Json(new
            {
                version = AppInfo.Version,
                connected = false,
                connecting = sensor.Connecting,
                historySampleCount = advisor.GetHistory().Count,
                forecast = outlook,
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
                historySampleCount = advisor.GetHistory().Count,
                forecast = outlook,
                advisory = advice.Note
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new
            {
                version = AppInfo.Version,
                connected = sensor.Connected,
                historySampleCount = advisor.GetHistory().Count,
                forecast = outlook,
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

    private static double? ParseNullableDouble(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException("Latitude and longitude must use decimal degrees with a decimal point.");
        }

        return value;
    }

    private static int ParseInt(string raw, string fieldName)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException($"{fieldName} must be a whole number.");
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

    private static string BuildHistoryChart(IReadOnlyList<DewHistoryPoint> history)
    {
        if (history.Count < 2)
        {
            return $"<div class=\"history-empty\">Collecting dew-margin history… {history.Count} sample{(history.Count == 1 ? string.Empty : "s")} recorded.</div>";
        }

        const double width = 900;
        const double height = 220;
        const double left = 48;
        const double right = 14;
        const double top = 14;
        const double bottom = 32;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;

        var firstAt = history[0].At;
        var lastAt = history[^1].At;
        var durationSeconds = Math.Max(1, (lastAt - firstAt).TotalSeconds);
        var minMargin = Math.Min(0, Math.Floor(history.Min(point => point.DewMarginC) - 0.5));
        var maxMargin = Math.Max(8, Math.Ceiling(history.Max(point => point.DewMarginC) + 0.5));
        var range = Math.Max(1, maxMargin - minMargin);

        double X(DateTimeOffset at) => left + ((at - firstAt).TotalSeconds / durationSeconds) * plotWidth;
        double Y(double margin) => top + ((maxMargin - margin) / range) * plotHeight;
        string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

        var points = string.Join(" ", history.Select(point => $"{F(X(point.At))},{F(Y(point.DewMarginC))}"));
        var svg = new System.Text.StringBuilder();
        svg.Append($"<svg class=\"history-chart\" viewBox=\"0 0 {F(width)} {F(height)}\" role=\"img\" aria-label=\"Dew margin history\">");

        foreach (var threshold in new[] { 0.0, 1.0, 2.0, 3.0, 5.0, 8.0 })
        {
            if (threshold < minMargin || threshold > maxMargin)
            {
                continue;
            }

            var y = Y(threshold);
            svg.Append($"<line class=\"history-grid\" x1=\"{F(left)}\" y1=\"{F(y)}\" x2=\"{F(width - right)}\" y2=\"{F(y)}\" />");
            svg.Append($"<text class=\"history-label\" x=\"{F(left - 7)}\" y=\"{F(y + 4)}\" text-anchor=\"end\">{threshold:0}°</text>");
        }

        svg.Append($"<polyline class=\"history-line\" points=\"{points}\" />");
        svg.Append($"<circle class=\"history-dot\" cx=\"{F(X(lastAt))}\" cy=\"{F(Y(history[^1].DewMarginC))}\" r=\"3.5\" />");
        svg.Append($"<text class=\"history-label\" x=\"{F(left)}\" y=\"{F(height - 7)}\">{firstAt.ToLocalTime():HH:mm}</text>");
        svg.Append($"<text class=\"history-label\" x=\"{F(width - right)}\" y=\"{F(height - 7)}\" text-anchor=\"end\">{lastAt.ToLocalTime():HH:mm}</text>");
        svg.Append("</svg>");
        return svg.ToString();
    }

    private static string BuildHistorySummary(IReadOnlyList<DewHistoryPoint> history)
    {
        if (history.Count == 0)
        {
            return "No samples yet. History starts when the dashboard service starts and is kept in memory for two hours.";
        }

        var span = history[^1].At - history[0].At;
        var spanText = span.TotalMinutes < 1
            ? "less than a minute"
            : span.TotalMinutes < 60
                ? $"{span.TotalMinutes:F0} min"
                : $"{span.TotalHours:F1} hr";

        return $"{history.Count} samples · {spanText} shown · current {history[^1].DewMarginC:F2} °C · in-memory only";
    }

    private static string BuildPage(SensorService sensor, AppConfig config, DewAdvisor advisor, ForecastService forecast, string message)
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

        var history = advisor.GetHistory();
        var historyChart = BuildHistoryChart(history);
        var historySummary = BuildHistorySummary(history);
        var outlook = forecast.Outlook;

        string forecastState;
        string overnightPower = "—";
        string overnightKnob = "—";
        string predictedMinimum = "—";
        string worstTime = "—";
        string localBias = "—";
        string forecastConfidence = outlook.Confidence;
        string forecastUpdated = outlook.UpdatedAt is DateTimeOffset forecastAt ? forecastAt.ToLocalTime().ToString("HH:mm") : "—";
        string forecastError = string.IsNullOrWhiteSpace(outlook.Error) ? string.Empty : WebUtility.HtmlEncode(outlook.Error);

        if (!config.ForecastEnabled)
        {
            forecastState = "Disabled";
        }
        else if (!outlook.Configured)
        {
            forecastState = "Needs location";
        }
        else if (outlook.Available)
        {
            forecastState = "Available";
            overnightPower = outlook.SessionPowerPercent is int sessionPower ? $"~{sessionPower}%" : "—";
            overnightKnob = outlook.SessionKnobPosition ?? "—";
            predictedMinimum = outlook.ConservativeMinimumMarginC is double minimum ? $"{minimum:F2} °C" : "—";
            worstTime = outlook.WorstAt is DateTimeOffset at ? at.ToLocalTime().ToString("HH:mm") : "—";
            localBias = outlook.LocalBiasC is double bias ? $"{bias:+0.00;-0.00;0.00} °C" : "—";
        }
        else
        {
            forecastState = "Waiting";
        }

        var lastError = string.IsNullOrWhiteSpace(sensor.LastError)
            ? "None"
            : WebUtility.HtmlEncode(sensor.LastError);

        var notice = string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : $"<div class=\"notice\">{WebUtility.HtmlEncode(message)}</div>";

        var forecastLatitude = config.ForecastLatitude?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;
        var forecastLongitude = config.ForecastLongitude?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;
        var forecastEnabledChecked = config.ForecastEnabled ? "checked" : string.Empty;
        var forecastEnsembleChecked = config.ForecastUseEnsemble ? "checked" : string.Empty;

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
            h1{margin-bottom:4px}h2{margin-top:0}.sub{opacity:.7;margin-top:0}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px;margin:22px 0}
            .card,.panel{border:1px solid var(--border);border-radius:12px;background:var(--panel);padding:16px}.label{font-size:.86rem;opacity:.7}.value{font-size:1.8rem;font-weight:650;margin-top:4px}.detail{font-size:.85rem;opacity:.75;margin-top:4px}
            .panel{margin:14px 0}.row{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px}label{display:block;font-size:.88rem;margin-bottom:4px}
            input{box-sizing:border-box;width:100%;padding:9px;border:1px solid var(--border);border-radius:7px}input[type=checkbox]{width:auto;margin-right:7px}button{padding:9px 14px;border-radius:7px;border:1px solid var(--border);cursor:pointer;margin-top:10px}
            code{background:#8882;padding:2px 5px;border-radius:4px}.notice{border-left:4px solid var(--good);padding:10px 12px;background:#2e9b5518;margin:16px 0;border-radius:6px}
            .small{font-size:.88rem;opacity:.75}.status{font-weight:650}.advisory{border-left:4px solid var(--warn)}.forecast{border-left:4px solid var(--good)}
            .history-chart{width:100%;height:auto;min-height:180px;display:block;margin-top:8px}.history-grid{stroke:currentColor;stroke-opacity:.15;stroke-width:1}.history-line{fill:none;stroke:currentColor;stroke-width:2.5;stroke-linejoin:round;stroke-linecap:round}.history-dot{fill:currentColor}.history-label{fill:currentColor;opacity:.62;font-size:12px}.history-empty{padding:28px 8px;text-align:center;opacity:.7}
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
            <div class="card"><div class="label">AstroZap estimate now</div><div class="value">{{heaterPower}}</div><div class="detail">Knob: {{knobPosition}}</div></div>
          </div>

          <div class="panel forecast">
            <h2>Overnight set-and-leave outlook</h2>
            <div class="grid">
              <div class="card"><div class="label">Overnight heater</div><div class="value">{{overnightPower}}</div><div class="detail">Knob: {{overnightKnob}}</div></div>
              <div class="card"><div class="label">Conservative minimum margin</div><div class="value">{{predictedMinimum}}</div><div class="detail">Worst around {{worstTime}}</div></div>
              <div class="card"><div class="label">Local forecast correction</div><div class="value">{{localBias}}</div><div class="detail">sensor minus forecast now</div></div>
            </div>
            <div class="small"><strong>Status:</strong> {{forecastState}} · <strong>confidence:</strong> {{forecastConfidence}} · <strong>updated:</strong> {{forecastUpdated}}</div>
            {{(string.IsNullOrWhiteSpace(forecastError) ? string.Empty : $"<div class=\"small\">Last forecast error: {forecastError}</div>")}}
            <p class="small">The overnight value is a high-water mark: it can rise if the forecast worsens, but it will not automatically fall during the session. The conservative margin uses the local sensor bias, the worst UKMO 2 km ensemble member when available, and the configured safety margin.</p>
            <form method="post" action="/forecast-refresh" style="display:inline"><button type="submit">Refresh forecast</button></form>
            <form method="post" action="/forecast-reset" style="display:inline"><button type="submit">Reset session high-water mark</button></form>
          </div>

          <div class="panel">
            <h2>Dew margin history</h2>
            <div class="small">{{historySummary}}</div>
            {{historyChart}}
          </div>

          <div class="panel advisory">
            <strong>Heater guidance is advisory.</strong>
            <div class="small">The estimates map dew margin to the AstroZap dual-channel controller's approximate 5–95% duty-cycle range. TemperHumAlpaca measures ambient air rather than the objective itself, so the forecast is a conservative starting setting rather than a guarantee against dew.</div>
          </div>

          <div class="panel">
            <strong>Status:</strong> <span class="status">{{status}}</span><br>
            <span class="small">Reading age: {{age}} · Raw sensor: {{rawTemperature}}, {{rawHumidity}} RH · Last error: {{lastError}}</span>
            <form method="post" action="/refresh" style="display:inline"><button type="submit">Refresh reading</button></form>
            <form method="post" action="/reconnect" style="display:inline"><button type="submit">Reconnect USB sensor</button></form>
          </div>

          <div class="panel">
            <h2>Overnight forecast settings</h2>
            <p class="small">Coordinates are stored only in the local <code>temperhum.json</code>. When forecasting is enabled they are sent to Open-Meteo to retrieve UK Met Office forecast data.</p>
            <form method="post" action="/forecast-settings">
              <label><input name="forecastEnabled" type="checkbox" {{forecastEnabledChecked}}>Enable overnight forecast</label>
              <label><input name="forecastUseEnsemble" type="checkbox" {{forecastEnsembleChecked}}>Use UKMO 2 km ensemble worst-member when available</label>
              <div class="row">
                <div><label for="forecastLatitude">Latitude</label><input id="forecastLatitude" name="forecastLatitude" type="number" step="0.000001" min="-90" max="90" value="{{forecastLatitude}}"></div>
                <div><label for="forecastLongitude">Longitude</label><input id="forecastLongitude" name="forecastLongitude" type="number" step="0.000001" min="-180" max="180" value="{{forecastLongitude}}"></div>
                <div><label for="forecastHours">Horizon (hours)</label><input id="forecastHours" name="forecastHours" type="number" min="6" max="24" step="1" value="{{config.ForecastHours}}" required></div>
                <div><label for="forecastRefreshMinutes">Refresh (minutes)</label><input id="forecastRefreshMinutes" name="forecastRefreshMinutes" type="number" min="5" max="180" step="1" value="{{config.ForecastRefreshMinutes}}" required></div>
                <div><label for="forecastSafetyMarginC">Extra safety margin (°C)</label><input id="forecastSafetyMarginC" name="forecastSafetyMarginC" type="number" min="0" max="5" step="0.1" value="{{config.ForecastSafetyMarginC.ToString("0.0", CultureInfo.InvariantCulture)}}" required></div>
              </div>
              <button type="submit">Save and refresh forecast</button>
            </form>
            <p class="small">Forecast data: Open-Meteo using UK Met Office UKV/ensemble models. UKV provides hourly 2 km data across the UK and Ireland.</p>
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
            <strong>Dashboard:</strong> localhost:{{config.DashboardPort}} · <strong>status API:</strong> <code>/api/v1/status</code> · <strong>history API:</strong> <code>/api/v1/history</code> · <strong>forecast API:</strong> <code>/api/v1/forecast</code><br>
            <strong>Unique ID:</strong> <code>{{WebUtility.HtmlEncode(config.UniqueId)}}</code><br>
            Configuration: <code>{{WebUtility.HtmlEncode(Path.Combine(AppContext.BaseDirectory, "temperhum.json"))}}</code>
          </div>
        </body>
        </html>
        """;
    }
}
