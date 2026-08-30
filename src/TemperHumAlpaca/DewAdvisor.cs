internal sealed record DewAdvice(
    double DewMarginC,
    string Risk,
    int RecommendedPowerPercent,
    string KnobPosition,
    string Trend,
    double? DewMarginTrendCPerHour,
    string Note);

internal sealed class DewAdvisor
{
    private readonly object _gate = new();
    private readonly List<(DateTimeOffset At, double MarginC)> _history = [];
    private int? _lastRecommendedPower;

    public async Task TrackAsync(SensorService sensor, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        while (!cancellationToken.IsCancellationRequested)
        {
            RecordCurrent(sensor);

            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public DewAdvice Evaluate(SensorSnapshot snapshot)
    {
        var margin = snapshot.TemperatureC - snapshot.DewPointC;
        double? trendPerHour;

        lock (_gate)
        {
            AddSample(snapshot.UpdatedAt, margin);
            trendPerHour = CalculateTrendPerHour(snapshot.UpdatedAt, margin);
        }

        var risk = RiskForMargin(margin);
        var basePower = BasePowerForMargin(margin);
        var trendAdjustment = TrendAdjustment(trendPerHour);
        var target = Math.Clamp(basePower + trendAdjustment, 5, 95);
        target = (int)(Math.Round(target / 5.0, MidpointRounding.AwayFromZero) * 5);

        lock (_gate)
        {
            // A small hysteresis band prevents the recommendation bouncing between
            // neighbouring settings when the margin is hovering around a threshold.
            if (_lastRecommendedPower is int previous && Math.Abs(target - previous) <= 5)
            {
                target = previous;
            }
            else
            {
                _lastRecommendedPower = target;
            }
        }

        return new DewAdvice(
            margin,
            risk,
            target,
            KnobPositionForPower(target),
            TrendLabel(trendPerHour),
            trendPerHour,
            "Advisory starting point only; the controller has no objective-temperature feedback.");
    }

    private void RecordCurrent(SensorService sensor)
    {
        if (!sensor.Connected)
        {
            return;
        }

        try
        {
            var snapshot = sensor.Snapshot;
            var margin = snapshot.TemperatureC - snapshot.DewPointC;
            lock (_gate)
            {
                AddSample(snapshot.UpdatedAt, margin);
            }
        }
        catch
        {
            // Tracking must never interfere with the Alpaca sensor service.
        }
    }

    private void AddSample(DateTimeOffset at, double margin)
    {
        if (_history.Count == 0 || at > _history[^1].At)
        {
            _history.Add((at, margin));
        }

        var cutoff = at - TimeSpan.FromHours(2);
        _history.RemoveAll(sample => sample.At < cutoff);
    }

    private double? CalculateTrendPerHour(DateTimeOffset now, double currentMargin)
    {
        var minimumAge = now - TimeSpan.FromMinutes(10);
        var preferredAge = now - TimeSpan.FromMinutes(30);

        var reference = _history
            .Where(sample => sample.At <= minimumAge)
            .OrderBy(sample => Math.Abs((sample.At - preferredAge).TotalSeconds))
            .FirstOrDefault();

        if (reference == default)
        {
            return null;
        }

        var hours = (now - reference.At).TotalHours;
        if (hours <= 0)
        {
            return null;
        }

        return (currentMargin - reference.MarginC) / hours;
    }

    private static string RiskForMargin(double margin) => margin switch
    {
        <= 0 => "DEW LIKELY",
        <= 1 => "VERY HIGH",
        <= 2 => "HIGH",
        <= 3 => "ELEVATED",
        <= 5 => "MODERATE",
        <= 8 => "LOW",
        _ => "VERY LOW"
    };

    private static int BasePowerForMargin(double margin) => margin switch
    {
        <= 0 => 95,
        <= 1 => 70,
        <= 2 => 50,
        <= 3 => 35,
        <= 5 => 25,
        <= 8 => 15,
        _ => 5
    };

    private static int TrendAdjustment(double? trendPerHour)
    {
        if (trendPerHour is null)
        {
            return 0;
        }

        // Negative means the dew margin is shrinking and dew risk is increasing.
        return trendPerHour.Value switch
        {
            <= -1.0 => 10,
            <= -0.3 => 5,
            >= 1.0 => -5,
            _ => 0
        };
    }

    private static string TrendLabel(double? trendPerHour)
    {
        if (trendPerHour is null)
        {
            return "Collecting history";
        }

        return trendPerHour.Value switch
        {
            <= -1.0 => "Falling quickly",
            <= -0.3 => "Falling",
            < 0.3 => "Stable",
            < 1.0 => "Rising",
            _ => "Rising quickly"
        };
    }

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
}
