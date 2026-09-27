namespace Mendeleev.Application.Configuration
{
    /// <summary>Public addresses and names of the service.</summary>
    public sealed class ServiceOptions
    {
        public const string SectionName = "Service";

        /// <summary>Service name shown to users and in the client profile.</summary>
        public string Name { get; init; } = "Mendeleev";

        /// <summary>Site domain: public pages, cabinet, <c>/pay/return</c>. No trailing slash.</summary>
        public string SiteBaseUrl { get; init; } = "http://localhost:8080";

        /// <summary>Unpublished technical domain for webhooks (ТЗ 40, «Домены и TLS»). No trailing slash.</summary>
        public string WebhookBaseUrl { get; init; } = "http://localhost:8080";

        /// <summary>Bot username without @, for links back to the bot.</summary>
        public string BotUsername { get; init; } = string.Empty;

        public string BotUrl => string.IsNullOrEmpty(BotUsername) ? string.Empty : $"https://t.me/{BotUsername}";
    }
}
