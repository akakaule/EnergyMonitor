using System.Net.Http.Json;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;
using Microsoft.Extensions.Options;

namespace EnergyMonitor.Web.Notifications;

public sealed class TelegramOptions
{
    public string BaseUrl { get; set; } = "https://api.telegram.org";

    /// <summary>Bot token from BotFather. Comes from Key Vault or user secrets.</summary>
    public string? BotToken { get; set; }

    /// <summary>Default recipients, used until recipients are saved in Settings.</summary>
    public string[] ChatIds { get; set; } = [];
}

/// <summary>
/// Telegram Bot API channel. The bot token is part of the request path, so this client's requests are
/// kept out of HTTP logs and traces (see Program.cs and ServiceDefaults), and failures report only a status.
/// </summary>
public sealed class TelegramChannel(IHttpClientFactory httpFactory, IOptionsMonitor<TelegramOptions> options) : INotificationChannel
{
    public const string HttpClientName = "telegram";

    public string Name => "telegram";

    public bool IsConfigured(NotificationSettings settings) =>
        !string.IsNullOrWhiteSpace(options.CurrentValue.BotToken) && settings.Recipients.Count > 0;

    public async Task SendAsync(NotificationMessage message, string recipient, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(o.BotToken))
        {
            throw new NotificationException("Telegram is not configured (bot token missing).");
        }

        var url = $"{o.BaseUrl.TrimEnd('/')}/bot{o.BotToken}/sendMessage";
        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient(HttpClientName).PostAsJsonAsync(url, new { chat_id = recipient, text = message.Text }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // The exception is not passed on: its message can carry the request URL, and the URL carries the token.
            throw new NotificationException("Telegram could not be reached.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // 400 (unknown chat) and 403 (bot blocked or removed) concern this recipient only and will not
                // go away by retrying. Anything else (bad token, 429, 5xx) may, so it stays retryable.
                var status = (int)response.StatusCode;
                throw new NotificationException($"Telegram answered {status}.", permanent: status is 400 or 403);
            }
        }
    }
}
