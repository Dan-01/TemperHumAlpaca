using System.Globalization;
using System.Text.Json;

internal sealed record ForecastPoint(
    DateTimeOffset At,
    double ForecastMarginC,
    double AdjustedMarginC,
    string Source);

internal sealed record ForecastOutlook(
    bool Enabled,
    bool Configured,
    bool Available,
    DateTimeOffset? UpdatedAt,
    string Source,
    double? LocalBiasC,
    double? DeterministicMinimumMarginC,
    double? ConservativeMinimumMarginC,
    DateTimeOffset? WorstAt,
    int? RecommendedPowerPercent,
    string? KnobPosition,
    int? SessionPowerPercent,
    string? SessionKnobPosition,
    string Confidence,
    string? Error,
    IReadOnlyList<ForecastPoint> Points);

internal sealed class ForecastService : IDisposable
{
    private const string DeterministicModel = "ukmo_uk_deterministic_2km";
    private const string EnsembleModel = "ukmo_uk_ensemble_2km";
    private const double EnsembleConservativePercentile = 0.10;

    private readonly object _gate = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private ForecastOutlook _outlook = Empty(enabled: false, configured: false);
    private int? _sessionPowerPercent;

    public ForecastOutlook Outlook
    {
        get
        {
            lock (_gate)
            {
                return _outlook;
            }
        }
    }

    public async Task TrackAsync(SensorService sensor, AppConfig config, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RefreshSafeAsync(sensor, config, cancellationToken);

            var delay = config.ForecastEnabled && IsConfigured(config)
                ? TimeSpan.FromMinutes(Math.Clamp(config.ForecastRefreshMinutes, 5, 180))
                : TimeSpan.FromMinutes(1);

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task RefreshNowAsync(SensorService sensor, AppConfig config, CancellationToken cancellationToken)
    {
        await RefreshSafeAsync(sensor, config, cancellationToken);
    }

    public void ResetSessionRecommendation()
    {
        lock (_gate)
        {
            _sessionPowerPercent = null;
            if (_outlook.Available && _outlook.RecommendedPowerPercent is int recommended)
            {
                _sessionPowerPercent = recommended;
                _outlook = _outlook with
                {
                    SessionPowerPercent = recommended,
                    SessionKnobPosition = KnobPositionForPower(recommended)
                };
            }
        }
    }

    private async Task RefreshSafeAsync(SensorService sensor, AppConfig config, CancellationToken cancellationToken)
    {
        if (!config.ForecastEnabled)
        {
            lock (_gate)
            {
                _sessionPowerPercent = null;
                _outlook = Empty(enabled: false, configured: IsConfigured(config));
            }
            return;
        }

        if (!IsConfigured(config))
        {
            lock (_gate)
            {
                _outlook = Empty(enabled: true, configured: false) with
                {
                    Error = "Set forecast latitude and longitude on the dashboard."
                };
            }
            return;
        }

        if (!sensor.Connected)
        {
            lock (_gate)
            {
                _outlook = Empty(enabled: true, configured: true) with
                {
                    Error = "Waiting for a local TEMPerHUM reading before bias-correcting the forecast."
                };
            }
            return;
        }

        try
        {
            var snapshot = sensor.Snapshot;
            var currentMargin = snapshot.TemperatureC - snapshot.DewPointC;
            var hours = Math.Clamp(config.ForecastHours, 6, 24);
            var deterministic = await FetchDeterministicAsync(config, hours, cancellationToken);
            if (deterministic.Count == 0)
            {
                throw new InvalidOperationException("UKMO forecast returned no hourly temperature/dew-point data.");
            }

            var now = DateTimeOffset.UtcNow;
            var currentForecast = deterministic
                .OrderBy(point => Math.Abs((point.At - now).TotalMinutes))
                .First();
            var localBias = currentMargin - currentForecast.ForecastMarginC;

            var futureDeterministic = deterministic
                .Where(point => point.At >= now - TimeSpan.FromMinutes(45))
                .ToList();
            if (futureDeterministic.Count == 0)
            {
                futureDeterministic = deterministic;
            }

            var deterministicAdjusted = futureDeterministic
                .Select(point => point with { AdjustedMarginC = point.ForecastMarginC + localBias })
                .ToList();
            var deterministicMinimum = deterministicAdjusted.Min(point => point.AdjustedMarginC);

            var source = "UKMO UKV 2 km deterministic";
            var confidence = "Moderate · deterministic fallback";
            var conservativePoints = deterministicAdjusted;
            var conservativeMinimumBeforeSafety = deterministicMinimum;
            DateTimeOffset worstAt = deterministicAdjusted.MinBy(point => point.AdjustedMarginC)!.At;

            if (config.ForecastUseEnsemble)
            {
                try
                {
                    var ensemble = await FetchEnsembleAsync(config, hours, cancellationToken);
                    if (ensemble.Count > 0)
                    {
                        var adjustedEnsemble = ensemble
                            .Where(point => point.At >= now - TimeSpan.FromMinutes(45))
                            .Select(point => point with { AdjustedMarginC = point.ForecastMarginC + localBias })
                            .ToList();

                        if (adjustedEnsemble.Count > 0)
                        {
                            var worstEnsemble = adjustedEnsemble.MinBy(point => point.AdjustedMarginC)!;
                            conservativeMinimumBeforeSafety = Math.Min(deterministicMinimum, worstEnsemble.AdjustedMarginC);
                            worstAt = conservativeMinimumBeforeSafety == worstEnsemble.AdjustedMarginC
                                ? worstEnsemble.At
                                : deterministicAdjusted.MinBy(point => point.AdjustedMarginC)!.At;
                            conservativePoints = adjustedEnsemble;
                            source = "UKMO UKV 2 km deterministic + ensemble P10";
                            confidence = "High · ensemble P10 + local bias";
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Ensemble is an enhancement, never a dependency. The deterministic
                    // forecast plus safety margin remains available if ensemble data fails.
                    confidence = "Moderate · ensemble unavailable, deterministic fallback";
                }
            }

            var safetyMargin = Math.Clamp(config.ForecastSafetyMarginC, 0.0, 5.0);
            var conservativeMinimum = Math.Min(currentMargin, conservativeMinimumBeforeSafety) - safetyMargin;
            var recommended = HeaterPowerForMargin(conservativeMinimum);

            lock (_gate)
            {
                _sessionPowerPercent = _sessionPowerPercent is int previous
                    ? Math.Max(previous, recommended)
                    : recommended;

                _outlook = new ForecastOutlook(
                    Enabled: true,
                    Configured: true,
                    Available: true,
                    UpdatedAt: DateTimeOffset.UtcNow,
                    Source: source,
                    LocalBiasC: localBias,
                    DeterministicMinimumMarginC: deterministicMinimum,
                    ConservativeMinimumMarginC: conservativeMinimum,
                    WorstAt: worstAt,
                    RecommendedPowerPercent: recommended,
                    KnobPosition: KnobPositionForPower(recommended),
                    SessionPowerPercent: _sessionPowerPercent,
                    SessionKnobPosition: KnobPositionForPower(_sessionPowerPercent.Value),
                    Confidence: confidence,
                    Error: null,
                    Points: conservativePoints);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                var previous = _outlook;
                _outlook = new ForecastOutlook(
                    Enabled: true,
                    Configured: true,
                    Available: previous.Available,
                    UpdatedAt: previous.UpdatedAt,
                    Source: previous.Source,
                    LocalBiasC: previous.LocalBiasC,
                    DeterministicMinimumMarginC: previous.DeterministicMinimumMarginC,
                    ConservativeMinimumMarginC: previous.ConservativeMinimumMarginC,
                    WorstAt: previous.WorstAt,
                    RecommendedPowerPercent: previous.RecommendedPowerPercent,
                    KnobPosition: previous.KnobPosition,
                    SessionPowerPercent: _sessionPowerPercent,
                    SessionKnobPosition: _sessionPowerPercent is int power ? KnobPositionForPower(power) : null,
                    Confidence: previous.Confidence,
                    Error: ex.Message,
                    Points: previous.Points);
            }
        }
    }

    private async Task<List<ForecastPoint>> FetchDeterministicAsync(AppConfig config, int hours, CancellationToken cancellationToken)
    {
        var url = BuildUrl(
            "https://api.open-meteo.com/v1/forecast",
            config,
            hours,
            DeterministicModel);
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseSingleSeries(doc.RootElement, "deterministic");
    }

    private async Task<List<ForecastPoint>> FetchEnsembleAsync(AppConfig config, int hours, CancellationToken cancellationToken)
    {
        var url = BuildUrl(
            "https://ensemble-api.open-meteo.com/v1/ensemble",
            config,
            hours,
            EnsembleModel);
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseEnsembleSeries(doc.RootElement);
    }

    private static string BuildUrl(string endpoint, AppConfig config, int hours, string model)
    {
        var latitude = config.ForecastLatitude!.Value.ToString("0.######", CultureInfo.InvariantCulture);
        var longitude = config.ForecastLongitude!.Value.ToString("0.######", CultureInfo.InvariantCulture);
        return $"{endpoint}?latitude={latitude}&longitude={longitude}" +
               $"&hourly=temperature_2m,dew_point_2m&models={model}" +
               $"&forecast_hours={hours + 1}&timezone=UTC";
    }

    private static List<ForecastPoint> ParseSingleSeries(JsonElement root, string source)
    {
        if (!root.TryGetProperty("hourly", out var hourly) ||
            !hourly.TryGetProperty("time", out var times) ||
            !hourly.TryGetProperty("temperature_2m", out var temperatures) ||
            !hourly.TryGetProperty("dew_point_2m", out var dewPoints))
        {
            return [];
        }

        var result = new List<ForecastPoint>();
        var count = Math.Min(times.GetArrayLength(), Math.Min(temperatures.GetArrayLength(), dewPoints.GetArrayLength()));
        for (var i = 0; i < count; i++)
        {
            if (!TryReadUtcTime(times[i], out var at) ||
                temperatures[i].ValueKind != JsonValueKind.Number ||
                dewPoints[i].ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var margin = temperatures[i].GetDouble() - dewPoints[i].GetDouble();
            result.Add(new ForecastPoint(at, margin, margin, source));
        }

        return result;
    }

    private static List<ForecastPoint> ParseEnsembleSeries(JsonElement root)
    {
        if (!root.TryGetProperty("hourly", out var hourly) ||
            !hourly.TryGetProperty("time", out var times))
        {
            return [];
        }

        var temperatures = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var dewPoints = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in hourly.EnumerateObject())
        {
            if (property.Name.StartsWith("temperature_2m_", StringComparison.OrdinalIgnoreCase))
            {
                temperatures[property.Name["temperature_2m_".Length..]] = property.Value;
            }
            else if (property.Name.StartsWith("dew_point_2m_", StringComparison.OrdinalIgnoreCase))
            {
                dewPoints[property.Name["dew_point_2m_".Length..]] = property.Value;
            }
        }

        var sharedMembers = temperatures.Keys
            .Where(dewPoints.ContainsKey)
            .ToList();
        if (sharedMembers.Count == 0)
        {
            return [];
        }

        var result = new List<ForecastPoint>();
        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            if (!TryReadUtcTime(times[i], out var at))
            {
                continue;
            }

            var margins = new List<double>();
            foreach (var member in sharedMembers)
            {
                var temperatureSeries = temperatures[member];
                var dewPointSeries = dewPoints[member];
                if (i >= temperatureSeries.GetArrayLength() || i >= dewPointSeries.GetArrayLength() ||
                    temperatureSeries[i].ValueKind != JsonValueKind.Number ||
                    dewPointSeries[i].ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                margins.Add(temperatureSeries[i].GetDouble() - dewPointSeries[i].GetDouble());
            }

            if (margins.Count == 0)
            {
                continue;
            }

            margins.Sort();
            var index = (int)Math.Floor((margins.Count - 1) * EnsembleConservativePercentile);
            var margin = margins[Math.Clamp(index, 0, margins.Count - 1)];
            result.Add(new ForecastPoint(at, margin, margin, "ensemble-p10"));
        }

        return result;
    }

    private static bool TryReadUtcTime(JsonElement element, out DateTimeOffset at)
    {
        at = default;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = element.GetString();
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out at);
    }

    private static bool IsConfigured(AppConfig config) =>
        config.ForecastLatitude is >= -90 and <= 90 &&
        config.ForecastLongitude is >= -180 and <= 180;

    private static int HeaterPowerForMargin(double margin) => margin switch
    {
        <= 0 => 95,
        <= 1 => 70,
        <= 2 => 50,
        <= 3 => 35,
        <= 5 => 25,
        <= 8 => 15,
        _ => 5
    };

    private static string KnobPositionForPower(int power) => power switch
    {
        <= 5 => "Low / minimum",
        <= 15 => "Just above Low",
        <= 25 => "About 1/4",
        <= 35 => "About 1/3",
        <= 50 => "About 1/2",
        <= 70 => "About 2/3",
        <= 85 => "About 3/4",
        _ => "High / maximum"
    };

    private static ForecastOutlook Empty(bool enabled, bool configured) => new(
        Enabled: enabled,
        Configured: configured,
        Available: false,
        UpdatedAt: null,
        Source: string.Empty,
        LocalBiasC: null,
        DeterministicMinimumMarginC: null,
        ConservativeMinimumMarginC: null,
        WorstAt: null,
        RecommendedPowerPercent: null,
        KnobPosition: null,
        SessionPowerPercent: null,
        SessionKnobPosition: null,
        Confidence: "Unavailable",
        Error: null,
        Points: []);

    public void Dispose() => _http.Dispose();
}
