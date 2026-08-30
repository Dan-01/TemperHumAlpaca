using CommunityToolkit.Mvvm.Input;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TemperHumAlpaca.NinaPlugin;

internal static class PluginIds
{
    public static readonly Guid Identifier = Guid.Parse("098dfea7-d111-43b4-ac5c-21ca0275e7e7");
}

[Export(typeof(IPluginManifest))]
public sealed class TemperHumPlugin : PluginBase, INotifyPropertyChanged
{
    internal const string TelegramTokenSettingKey = "TelegramBotTokenProtected";
    private static readonly byte[] TelegramTokenEntropy = Encoding.UTF8.GetBytes("TemperHumAlpaca.NinaPlugin.Telegram.v1");

    private readonly IProfileService _profileService;
    private string _telegramTestStatus = string.Empty;

    [ImportingConstructor]
    public TemperHumPlugin(IProfileService profileService)
    {
        _profileService = profileService;
        PluginSettings = new PluginOptionsAccessor(profileService, PluginIds.Identifier);
        _profileService.ProfileChanged += ProfileService_ProfileChanged;

        TestTelegramCommand = new AsyncRelayCommand(TestTelegramAsync);
        ClearTelegramBotTokenCommand = new RelayCommand(ClearTelegramBotToken);
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

    public bool TelegramEnabled
    {
        get => PluginSettings.GetValueBoolean(nameof(TelegramEnabled), false);
        set
        {
            PluginSettings.SetValueBoolean(nameof(TelegramEnabled), value);
            RaisePropertyChanged();
        }
    }

    public string TelegramChatId
    {
        get => PluginSettings.GetValueString(nameof(TelegramChatId), string.Empty).Trim();
        set
        {
            PluginSettings.SetValueString(nameof(TelegramChatId), (value ?? string.Empty).Trim());
            RaisePropertyChanged();
        }
    }

    public bool TelegramBotTokenConfigured =>
        !string.IsNullOrWhiteSpace(PluginSettings.GetValueString(TelegramTokenSettingKey, string.Empty));

    public string TelegramTokenStatus => TelegramBotTokenConfigured
        ? "Bot token configured · encrypted for this Windows user"
        : "Bot token not configured";

    public string TelegramTestStatus
    {
        get => _telegramTestStatus;
        private set
        {
            _telegramTestStatus = value;
            RaisePropertyChanged();
        }
    }

    public ICommand TestTelegramCommand { get; }
    public ICommand ClearTelegramBotTokenCommand { get; }

    public void SetTelegramBotToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var plainBytes = Encoding.UTF8.GetBytes(token.Trim());
        var protectedBytes = ProtectedData.Protect(plainBytes, TelegramTokenEntropy, DataProtectionScope.CurrentUser);
        PluginSettings.SetValueString(TelegramTokenSettingKey, Convert.ToBase64String(protectedBytes));
        TelegramTestStatus = string.Empty;
        RaisePropertyChanged(nameof(TelegramBotTokenConfigured));
        RaisePropertyChanged(nameof(TelegramTokenStatus));
    }

    public void ClearTelegramBotToken()
    {
        PluginSettings.SetValueString(TelegramTokenSettingKey, string.Empty);
        TelegramTestStatus = "Stored bot token cleared.";
        RaisePropertyChanged(nameof(TelegramBotTokenConfigured));
        RaisePropertyChanged(nameof(TelegramTokenStatus));
    }

    internal static string ReadTelegramBotToken(IPluginOptionsAccessor settings)
    {
        var protectedValue = settings.GetValueString(TelegramTokenSettingKey, string.Empty);
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return string.Empty;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedValue);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, TelegramTokenEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task TestTelegramAsync()
    {
        var token = ReadTelegramBotToken(PluginSettings);
        if (string.IsNullOrWhiteSpace(token))
        {
            TelegramTestStatus = "Bot token is not configured.";
            return;
        }

        var chatId = TelegramChatId;
        if (string.IsNullOrWhiteSpace(chatId))
        {
            TelegramTestStatus = "Chat ID is not configured.";
            return;
        }

        TelegramTestStatus = "Sending test…";
        try
        {
            await TelegramNotifier.SendAsync(
                token,
                chatId,
                $"🔭 TemperHumAlpaca v0.6 Telegram test\nN.I.N.A. remote dew alerts are configured on {Environment.MachineName}.",
                default);
            TelegramTestStatus = $"Test message sent at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            TelegramTestStatus = $"Telegram test failed: {ex.Message}";
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
