using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace TemperHumAlpaca.NinaPlugin;

internal static class PluginIds
{
    public static readonly Guid Identifier = Guid.Parse("098dfea7-d111-43b4-ac5c-21ca0275e7e7");
}

[Export(typeof(IPluginManifest))]
public sealed class TemperHumPlugin : PluginBase, INotifyPropertyChanged
{
    private readonly IProfileService _profileService;

    [ImportingConstructor]
    public TemperHumPlugin(IProfileService profileService)
    {
        _profileService = profileService;
        PluginSettings = new PluginOptionsAccessor(profileService, PluginIds.Identifier);
        _profileService.ProfileChanged += ProfileService_ProfileChanged;
    }

    public IPluginOptionsAccessor PluginSettings { get; }

    public IReadOnlyList<string> AlertRiskLevels { get; } =
        new[] { "Elevated", "High", "Very high", "Dew likely" };

    public string ServiceUrl
    {
        get => NormalizeServiceUrl(PluginSettings.GetValueString(nameof(ServiceUrl), "http://127.0.0.1:11112"));
        set
        {
            PluginSettings.SetValueString(nameof(ServiceUrl), NormalizeServiceUrl(value));
            RaisePropertyChanged();
        }
    }

    public int PollIntervalSeconds
    {
        get => Math.Clamp(PluginSettings.GetValueInt32(nameof(PollIntervalSeconds), 5), 2, 60);
        set
        {
            PluginSettings.SetValueInt32(nameof(PollIntervalSeconds), Math.Clamp(value, 2, 60));
            RaisePropertyChanged();
        }
    }

    public bool AlertsEnabled
    {
        get => PluginSettings.GetValueBoolean(nameof(AlertsEnabled), true);
        set
        {
            PluginSettings.SetValueBoolean(nameof(AlertsEnabled), value);
            RaisePropertyChanged();
        }
    }

    public string AlertRiskLevel
    {
        get
        {
            var configured = PluginSettings.GetValueString(nameof(AlertRiskLevel), "High");
            foreach (var risk in AlertRiskLevels)
            {
                if (risk.Equals(configured, StringComparison.OrdinalIgnoreCase))
                {
                    return risk;
                }
            }
            return "High";
        }
        set
        {
            PluginSettings.SetValueString(nameof(AlertRiskLevel), value ?? "High");
            RaisePropertyChanged();
        }
    }

    public bool AlertOnHeaterIncrease
    {
        get => PluginSettings.GetValueBoolean(nameof(AlertOnHeaterIncrease), true);
        set
        {
            PluginSettings.SetValueBoolean(nameof(AlertOnHeaterIncrease), value);
            RaisePropertyChanged();
        }
    }

    public bool AlertOnSensorDisconnect
    {
        get => PluginSettings.GetValueBoolean(nameof(AlertOnSensorDisconnect), true);
        set
        {
            PluginSettings.SetValueBoolean(nameof(AlertOnSensorDisconnect), value);
            RaisePropertyChanged();
        }
    }

    public bool AlertOnServiceLoss
    {
        get => PluginSettings.GetValueBoolean(nameof(AlertOnServiceLoss), true);
        set
        {
            PluginSettings.SetValueBoolean(nameof(AlertOnServiceLoss), value);
            RaisePropertyChanged();
        }
    }

    public int AlertCooldownMinutes
    {
        get => Math.Clamp(PluginSettings.GetValueInt32(nameof(AlertCooldownMinutes), 15), 1, 180);
        set
        {
            PluginSettings.SetValueInt32(nameof(AlertCooldownMinutes), Math.Clamp(value, 1, 180));
            RaisePropertyChanged();
        }
    }

    public override Task Teardown()
    {
        _profileService.ProfileChanged -= ProfileService_ProfileChanged;
        return base.Teardown();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void ProfileService_ProfileChanged(object? sender, EventArgs e) => RaisePropertyChanged(null);

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    internal static string NormalizeServiceUrl(string? value)
    {
        var url = string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:11112" : value.Trim();
        return url.TrimEnd('/');
    }
}
