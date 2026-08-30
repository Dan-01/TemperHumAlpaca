using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TemperHumAlpaca.NinaPlugin;

internal static class TelegramNotifier
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public static async Task SendAsync(string botToken, string chatId, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new InvalidOperationException("Telegram bot token is not configured.");
        }

        if (string.IsNullOrWhiteSpace(chatId))
        {
            throw new InvalidOperationException("Telegram chat ID is not configured.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Telegram message cannot be empty.", nameof(message));
        }

        var endpoint = $"https://api.telegram.org/bot{botToken.Trim()}/sendMessage";
        using var response = await Http.PostAsJsonAsync(
            endpoint,
            new
            {
                chat_id = chatId.Trim(),
                text = message
            },
            cancellationToken).ConfigureAwait(false);

        TelegramResponse? result = null;
        try
        {
            result = await response.Content.ReadFromJsonAsync<TelegramResponse>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Preserve the HTTP status as the useful fallback when Telegram did not
            // return a normal Bot API JSON envelope.
        }

        if (!response.IsSuccessStatusCode || result?.Ok != true)
        {
            var detail = string.IsNullOrWhiteSpace(result?.Description)
                ? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
                : result!.Description;
            throw new InvalidOperationException(detail);
        }
    }

    private sealed class TelegramResponse
    {
        public bool Ok { get; set; }
        public string? Description { get; set; }
    }
}
