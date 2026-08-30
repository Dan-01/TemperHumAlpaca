using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TemperHumAlpaca.NinaPlugin;

[Export(typeof(IDockableVM))]
public sealed class TemperHumDockable : DockableVM, IDisposable
{
    private readonly IPluginOptionsAccessor _settings;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _pollCts = new();

    private bool _hasSuccessfulReading;
    private bool _serviceOnline;
    private bool? _lastSensorConnected;
    private int? _lastRiskRank;
    private int? _lastSessionPower;
    private DateTimeOffset _lastServiceAlert = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSensorAlert = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRiskAlert = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHeaterAlert = DateTimeOffset.MinValue;

    private string _serviceStatus = "Waiting for TemperHumAlpaca…";
    private string _temperature = "—";
    private string _humidity = "—";
    private string _dewPoint = "—";
    private string _dewMargin = "—";
    private string _trend = "—";
    private string _dewRisk = "—";
    private string _currentHeater = "—";
    private string _currentKnob = "—";
    private string _overnightHeater = "—";
    private string _overnightKnob = "—";
    private string _forecastMinimum = "—";
    private string _forecastWorstTime = "—";
    private string _forecastDetail = "Forecast unavailable";
    private string _lastUpdated = "Not updated yet";
    private string _lastError = string.Empty;

    public override bool IsTool { get; } = true;

    [ImportingConstructor]
    public TemperHumDockable(IProfileService profileService) : base(profileService)
    {
        Title = "TEMPerHUM Dew Monitor";
        ImageGeometry = BuildIcon();
        _settings = new PluginOptionsAccessor(profileService, PluginIds.Identifier);

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None));
        OpenDashboardCommand = new RelayCommand(OpenDashboard);

        _ = PollLoopAsync(_pollCts.Token);
    }

    public ICommand RefreshCommand { get; }
    public ICommand OpenDashboardCommand { get; }

    public string ServiceStatus { get => _serviceStatus; private set => Set(ref _serviceStatus, value); }
    public string Temperature { get => _temperature; private set => Set(ref _temperature, value); }
    public string Humidity { get => _humidity; private set => Set(ref _humidity, value); }
    public string DewPoint { get => _dewPoint; private set => Set(ref _dewPoint, value); }
    public string DewMargin { get => _dewMargin; private set => Set(ref _dewMargin, value); }
    public string Trend { get => _trend; private set => Set(ref _trend, value); }
    public string DewRisk { get => _dewRisk; private set => Set(ref _dewRisk, value); }
    public string CurrentHeater { get => _currentHeater; private set => Set(ref _currentHeater, value); }
    public string CurrentKnob { get => _currentKnob; private set => Set(ref _currentKnob, value); }
    public string OvernightHeater { get => _overnightHeater; private set => Set(ref _overnightHeater, value); }
    public string OvernightKnob { get => _overnightKnob; private set => Set(ref _overnightKnob, value); }
    public string ForecastMinimum { get => _forecastMinimum; private set => Set(ref _forecastMinimum, value); }
    public string ForecastWorstTime { get => _forecastWorstTime; private set => Set(ref _forecastWorstTime, value); }
    public string ForecastDetail { get => _forecastDetail; private set => Set(ref _forecastDetail, value); }
    public string LastUpdated { get => _lastUpdated; private set => Set(ref _lastUpdated, value); }
    public string LastError { get => _lastError; private set => Set(ref _lastError, value); }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await RefreshAsync(token).ConfigureAwait(false);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(GetPollIntervalSeconds()), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        if (!await _refreshGate.WaitAsync(0, token).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var serviceUrl = TemperHumPlugin.NormalizeServiceUrl(
                _settings.GetValueString(nameof(TemperHumPlugin.ServiceUrl), "http://127.0.0.1:11112"));
            var endpoint = $"{serviceUrl}/api/v1/status";

            using var response = await _httpClient.GetAsync(endpoint, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var status = await response.Content.ReadFromJsonAsync<StatusDto>(JsonOptions, token).ConfigureAwait(false);
            if (status is null)
            {
                throw new InvalidOperationException("TemperHumAlpaca returned an empty status response.");
            }

            await OnUiAsync(() => ApplyStatus(status)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await OnUiAsync(() => ApplyServiceFailure(ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ApplyStatus(StatusDto status)
    {
        var previouslyOnline = _serviceOnline;
        _serviceOnline = true;

        ServiceStatus = status.Connected
            ? $"Service v{status.Version ?? "?"} · sensor connected"
            : status.Connecting
                ? $"Service v{status.Version ?? "?"} · sensor connecting"
                : $"Service v{status.Version ?? "?"} · sensor disconnected";

        LastError = string.IsNullOrWhiteSpace(status.LastError) ? string.Empty : $"Sensor: {status.LastError}";

        if (status.Connected)
        {
            Temperature = FormatC(status.TemperatureC);
            Humidity = status.HumidityPercent is double humidity ? $"{humidity:F1} %" : "—";
            DewPoint = FormatC(status.DewPointC);
            DewMargin = FormatC(status.DewMarginC);
            DewRisk = DisplayRisk(status.DewRisk);
            CurrentHeater = status.RecommendedHeaterPowerPercent is int power ? $"~{power}%" : "—";
            CurrentKnob = string.IsNullOrWhiteSpace(status.AstroZapKnobPosition) ? "—" : status.AstroZapKnobPosition!;
            Trend = FormatTrend(status.DewMarginTrend, status.DewMarginTrendCPerHour);
        }
        else
        {
            Temperature = Humidity = DewPoint = DewMargin = "—";
            DewRisk = "Sensor unavailable";
            CurrentHeater = CurrentKnob = Trend = "—";
        }

        ApplyForecast(status.Forecast);
        LastUpdated = status.UpdatedAt is DateTimeOffset readingAt
            ? $"Sensor reading {readingAt.ToLocalTime():HH:mm:ss} · panel refreshed {DateTime.Now:HH:mm:ss}"
            : $"Panel refreshed {DateTime.Now:HH:mm:ss}";

        EvaluateAlerts(status, previouslyOnline);
        _hasSuccessfulReading = true;
        _lastSensorConnected = status.Connected;
        _lastRiskRank = RiskRank(status.DewRisk);
        _lastSessionPower = status.Forecast?.SessionPowerPercent;
    }

    private void ApplyForecast(ForecastDto? forecast)
    {
        if (forecast is null || !forecast.Enabled)
        {
            OvernightHeater = OvernightKnob = ForecastMinimum = ForecastWorstTime = "—";
            ForecastDetail = "Overnight forecast disabled in TemperHumAlpaca.";
            return;
        }

        if (!forecast.Available)
        {
            OvernightHeater = OvernightKnob = ForecastMinimum = ForecastWorstTime = "—";
            ForecastDetail = string.IsNullOrWhiteSpace(forecast.Error)
                ? "Waiting for overnight forecast."
                : $"Forecast: {forecast.Error}";
            return;
        }

        OvernightHeater = forecast.SessionPowerPercent is int sessionPower ? $"~{sessionPower}%" : "—";
        OvernightKnob = forecast.SessionKnobPosition ?? "—";
        ForecastMinimum = FormatC(forecast.ConservativeMinimumMarginC);
        ForecastWorstTime = forecast.WorstAt is DateTimeOffset worst
            ? $"Worst around {worst.ToLocalTime():HH:mm}"
            : "Worst time unavailable";

        var bias = forecast.LocalBiasC is double localBias
            ? $"local correction {localBias:+0.00;-0.00;0.00} °C"
            : "local correction unavailable";
        var confidence = string.IsNullOrWhiteSpace(forecast.Confidence) ? "confidence unavailable" : forecast.Confidence;
        ForecastDetail = $"{confidence} · {bias}";
        if (!string.IsNullOrWhiteSpace(forecast.Error))
        {
            ForecastDetail += $" · last error: {forecast.Error}";
        }
    }

    private void ApplyServiceFailure(string error)
    {
        var wasOnline = _serviceOnline;
        _serviceOnline = false;
        ServiceStatus = "TemperHumAlpaca service unavailable";
        LastError = error;
        LastUpdated = $"Last attempt {DateTime.Now:HH:mm:ss}";

        if (_hasSuccessfulReading && wasOnline &&
            GetBool(nameof(TemperHumPlugin.AlertsEnabled), true) &&
            GetBool(nameof(TemperHumPlugin.AlertOnServiceLoss), true))
        {
            ShowWarningWithCooldown(
                ref _lastServiceAlert,
                "TemperHumAlpaca service is no longer reachable. The N.I.N.A. dew panel cannot update until it returns.");
        }
    }

    private void EvaluateAlerts(StatusDto status, bool previouslyOnline)
    {
        if (!GetBool(nameof(TemperHumPlugin.AlertsEnabled), true) || !_hasSuccessfulReading)
        {
            return;
        }

        if (previouslyOnline && _lastSensorConnected == true && !status.Connected &&
            GetBool(nameof(TemperHumPlugin.AlertOnSensorDisconnect), true))
        {
            ShowWarningWithCooldown(
                ref _lastSensorAlert,
                "TEMPerHUM sensor disconnected. TemperHumAlpaca is still running and will continue its USB reconnect attempts.");
        }

        var riskRank = RiskRank(status.DewRisk);
        var thresholdRank = RiskRank(GetString(nameof(TemperHumPlugin.AlertRiskLevel), "High"));
        if (status.Connected && riskRank >= thresholdRank && riskRank > (_lastRiskRank ?? 0))
        {
            ShowWarningWithCooldown(
                ref _lastRiskAlert,
                $"Dew risk increased to {DisplayRisk(status.DewRisk)}. Current dew margin is {FormatC(status.DewMarginC)}; heater estimate {FormatPercent(status.RecommendedHeaterPowerPercent)}.");
        }

        if (GetBool(nameof(TemperHumPlugin.AlertOnHeaterIncrease), true) &&
            status.Forecast?.SessionPowerPercent is int sessionPower &&
            _lastSessionPower is int previousPower &&
            sessionPower > previousPower)
        {
            ShowWarningWithCooldown(
                ref _lastHeaterAlert,
                $"Overnight AstroZap recommendation increased from ~{previousPower}% to ~{sessionPower}% ({status.Forecast.SessionKnobPosition ?? "knob position unavailable"}).");
        }
    }

    private void ShowWarningWithCooldown(ref DateTimeOffset lastAlert, string message)
    {
        var cooldown = TimeSpan.FromMinutes(Math.Clamp(
            _settings.GetValueInt32(nameof(TemperHumPlugin.AlertCooldownMinutes), 15), 1, 180));
        var now = DateTimeOffset.UtcNow;
        if (now - lastAlert < cooldown)
        {
            return;
        }

        lastAlert = now;
        Notification.ShowWarning(message);
        _ = SendTelegramAlertSafeAsync(message);
    }

    private async Task SendTelegramAlertSafeAsync(string message)
    {
        if (!GetBool(nameof(TemperHumPlugin.TelegramEnabled), false))
        {
            return;
        }

        var chatId = GetString(nameof(TemperHumPlugin.TelegramChatId), string.Empty).Trim();
        var token = TemperHumPlugin.ReadTelegramBotToken(_settings);
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        try
        {
            await TelegramNotifier.SendAsync(
                token,
                chatId,
                $"🔭 TemperHumAlpaca\n⚠️ {message}",
                _pollCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_pollCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await OnUiAsync(() =>
                Notification.ShowWarning($"TemperHumAlpaca Telegram delivery failed: {ex.Message}"))
                .ConfigureAwait(false);
        }
    }

    private void OpenDashboard()
    {
        try
        {
            var url = TemperHumPlugin.NormalizeServiceUrl(
                _settings.GetValueString(nameof(TemperHumPlugin.ServiceUrl), "http://127.0.0.1:11112"));
            Process.Start(new ProcessStartInfo($"{url}/dashboard") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notification.ShowError($"Could not open TemperHumAlpaca dashboard: {ex.Message}");
        }
    }

    private int GetPollIntervalSeconds() => Math.Clamp(
        _settings.GetValueInt32(nameof(TemperHumPlugin.PollIntervalSeconds), 5), 2, 60);

    private bool GetBool(string key, bool defaultValue) => _settings.GetValueBoolean(key, defaultValue);
    private string GetString(string key, string defaultValue) => _settings.GetValueString(key, defaultValue);

    private static int RiskRank(string? risk)
    {
        var normalized = (risk ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "dew likely" => 7,
            "very high" => 6,
            "high" => 5,
            "elevated" => 4,
            "moderate" => 3,
            "low" => 2,
            "very low" => 1,
            _ => 0
        };
    }

    private static string DisplayRisk(string? risk) =>
        string.IsNullOrWhiteSpace(risk) ? "—" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(risk.ToLowerInvariant());

    private static string FormatC(double? value) => value is double number ? $"{number:F2} °C" : "—";
    private static string FormatPercent(int? value) => value is int number ? $"~{number}%" : "—";

    private static string FormatTrend(string? trend, double? rate)
    {
        if (string.IsNullOrWhiteSpace(trend))
        {
            return "—";
        }

        return rate is double value
            ? $"{trend} ({value:+0.00;-0.00;0.00} °C/hr)"
            : trend;
    }

    private static GeometryGroup BuildIcon()
    {
        var group = new GeometryGroup();
        group.Children.Add(Geometry.Parse("M8,2 A6,6 0 1 0 8,14 A6,6 0 1 0 8,2 M8,5 L8,9 M8,11 L8,11.2"));
        group.Freeze();
        return group;
    }

    private static Task OnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private void Set(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }
        field = value;
        RaisePropertyChanged(propertyName);
    }

    public void Dispose()
    {
        _pollCts.Cancel();
        _pollCts.Dispose();
        _refreshGate.Dispose();
        _httpClient.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class StatusDto
    {
        public string? Version { get; set; }
        public bool Connected { get; set; }
        public bool Connecting { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public double? TemperatureC { get; set; }
        public double? HumidityPercent { get; set; }
        public double? DewPointC { get; set; }
        public double? DewMarginC { get; set; }
        public string? DewRisk { get; set; }
        public int? RecommendedHeaterPowerPercent { get; set; }
        public string? AstroZapKnobPosition { get; set; }
        public string? DewMarginTrend { get; set; }
        public double? DewMarginTrendCPerHour { get; set; }
        public string? LastError { get; set; }
        public ForecastDto? Forecast { get; set; }
    }

    private sealed class ForecastDto
    {
        public bool Enabled { get; set; }
        public bool Configured { get; set; }
        public bool Available { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public string? Source { get; set; }
        public double? LocalBiasC { get; set; }
        public double? ConservativeMinimumMarginC { get; set; }
        public DateTimeOffset? WorstAt { get; set; }
        public int? RecommendedPowerPercent { get; set; }
        public string? KnobPosition { get; set; }
        public int? SessionPowerPercent { get; set; }
        public string? SessionKnobPosition { get; set; }
        public string? Confidence { get; set; }
        public string? Error { get; set; }
    }
}
