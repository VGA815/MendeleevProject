using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mendeleev.Infrastructure.Alerts
{
    public sealed class AlertOptions
    {
        public const string SectionName = "Alerts";

        /// <summary>A separate alert bot, not the customer bot (ТЗ 30, «Алерты»).</summary>
        public string? TelegramBotToken { get; init; }

        public long[] TechAdminChatIds { get; init; } = [];

        public long[] AdminChatIds { get; init; } = [];

        /// <summary>Second channel in case Telegram is down, e.g. <c>https://ntfy.sh/mendeleev-alerts-XYZ</c>.</summary>
        public string? NtfyTopicUrl { get; init; }

        public string? NtfyToken { get; init; }

        /// <summary>The same alert key is not repeated within this window.</summary>
        public TimeSpan QuietWindow { get; init; } = TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// Sends alerts to the alert bot and duplicates them to ntfy. Swallows its own failures: an alert that
    /// cannot be delivered must not break the operation that raised it.
    /// </summary>
    internal sealed class AlertSink(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AlertOptions> options,
        IDateTimeProvider clock,
        ILogger<AlertSink> logger)
        : IAlertSink
    {
        public const string TelegramClientName = "alerts-telegram";
        public const string NtfyClientName = "alerts-ntfy";

        private static readonly ConcurrentDictionary<string, DateTime> LastRaised = new();
        private static readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> Series = new();

        public async Task RaiseAsync(Alert alert, CancellationToken cancellationToken = default)
        {
            DateTime now = clock.UtcNow;
            AlertOptions settings = options.CurrentValue;

            if (LastRaised.TryGetValue(alert.Key, out DateTime last) && now - last < settings.QuietWindow)
            {
                return;
            }
            LastRaised[alert.Key] = now;

            logger.Log(
                alert.Severity == AlertSeverity.Critical ? LogLevel.Error : LogLevel.Warning,
                "ALERT {AlertKey}: {AlertText}",
                alert.Key,
                alert.Text);

            string icon = alert.Severity switch
            {
                AlertSeverity.Critical => "🔴",
                AlertSeverity.Warning => "🟡",
                _ => "🔵",
            };
            string text = $"{icon} {alert.Text}";

            IEnumerable<long> chats = alert.Audience == AlertAudience.TechAdminAndAdmin
                ? settings.TechAdminChatIds.Concat(settings.AdminChatIds).Distinct()
                : settings.TechAdminChatIds;

            await Task.WhenAll(
                SendTelegramAsync(settings, chats, text, cancellationToken),
                SendNtfyAsync(settings, alert, cancellationToken));
        }

        public Task RaiseOnSeriesAsync(Alert alert, int threshold, TimeSpan window, CancellationToken cancellationToken = default)
        {
            DateTime now = clock.UtcNow;
            ConcurrentQueue<DateTime> hits = Series.GetOrAdd(alert.Key, _ => new ConcurrentQueue<DateTime>());
            hits.Enqueue(now);
            while (hits.TryPeek(out DateTime oldest) && now - oldest > window)
            {
                hits.TryDequeue(out _);
            }

            return hits.Count >= threshold ? RaiseAsync(alert, cancellationToken) : Task.CompletedTask;
        }

        private async Task SendTelegramAsync(AlertOptions settings, IEnumerable<long> chats, string text, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(settings.TelegramBotToken))
            {
                return;
            }

            HttpClient client = httpClientFactory.CreateClient(TelegramClientName);
            foreach (long chatId in chats)
            {
                try
                {
                    using HttpResponseMessage response = await client.PostAsJsonAsync(
                        $"https://api.telegram.org/bot{settings.TelegramBotToken}/sendMessage",
                        new { chat_id = chatId, text, disable_web_page_preview = true },
                        cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        logger.LogWarning("Alert bot returned {Status}", (int)response.StatusCode);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    logger.LogWarning(ex, "Alert not delivered to Telegram");
                }
            }
        }

        private async Task SendNtfyAsync(AlertOptions settings, Alert alert, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(settings.NtfyTopicUrl))
            {
                return;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, settings.NtfyTopicUrl)
                {
                    Content = new StringContent(alert.Text, Encoding.UTF8),
                };
                request.Headers.Add("Title", alert.Key);
                request.Headers.Add("Priority", alert.Severity == AlertSeverity.Critical ? "urgent" : "default");
                if (!string.IsNullOrWhiteSpace(settings.NtfyToken))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.NtfyToken);
                }

                using HttpResponseMessage response = await httpClientFactory.CreateClient(NtfyClientName).SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Alert not delivered to ntfy");
            }
        }
    }
}
