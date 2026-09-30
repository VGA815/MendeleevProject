namespace Mendeleev.Infrastructure.Telegram
{
    public sealed class TelegramOptions
    {
        public const string SectionName = "Telegram";

        public string BotToken { get; init; } = string.Empty;

        /// <summary>Compared with <c>X-Telegram-Bot-Api-Secret-Token</c> on every webhook call (FR-BOT-01).</summary>
        public string WebhookSecret { get; init; } = string.Empty;

        /// <summary>
        /// <see cref="TelegramUpdateMode.Webhook"/> normally; <see cref="TelegramUpdateMode.Polling"/> if
        /// incoming webhooks to the Russian DC turn out unreliable — switched without a code change (FR-BOT-16).
        /// </summary>
        public TelegramUpdateMode Mode { get; init; } = TelegramUpdateMode.Webhook;

        /// <summary>HTTP or SOCKS5 proxy abroad for outgoing Bot API calls, e.g. <c>socks5://user:pass@host:1080</c>.</summary>
        public string? ProxyUrl { get; init; }

        /// <summary>
        /// A Bot API server other than api.telegram.org: a local Bot API server, or the stub of the load test on
        /// staging (tools/Mendeleev.LoadTest). Empty — Telegram itself.
        /// </summary>
        public string? ApiBaseUrl { get; init; }

        /// <summary>Register the webhook (and commands) with Telegram at startup.</summary>
        public bool RegisterOnStartup { get; init; } = true;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken);
    }

    public enum TelegramUpdateMode
    {
        Webhook,
        Polling,
    }
}
