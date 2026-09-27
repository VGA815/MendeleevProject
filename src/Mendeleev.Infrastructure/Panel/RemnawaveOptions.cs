namespace Mendeleev.Infrastructure.Panel
{
    public sealed class RemnawaveOptions
    {
        public const string SectionName = "Remnawave";

        /// <summary>Panel URL abroad; its API is open only to the management VPS IP (ТЗ 10, решение 24.09).</summary>
        public string BaseUrl { get; init; } = string.Empty;

        /// <summary>A token created in the panel for this service only (FR-PNL-01).</summary>
        public string ApiToken { get; init; } = string.Empty;

        /// <summary>Same value as <c>WEBHOOK_SECRET_HEADER</c> in the panel environment.</summary>
        public string WebhookSecret { get; init; } = string.Empty;

        /// <summary>Webhooks older than this are rejected (FR-PNL-13).</summary>
        public TimeSpan WebhookMaxAge { get; init; } = TimeSpan.FromMinutes(5);

        public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

        public int PageSize { get; init; } = 250;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiToken);
    }
}
