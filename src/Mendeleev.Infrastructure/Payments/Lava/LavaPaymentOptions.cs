namespace Mendeleev.Infrastructure.Payments.Lava
{
    public sealed class LavaPaymentOptions
    {
        public const string SectionName = "Payments:Lava";

        /// <summary>Registers the provider. The keys come from the project settings in the Lava cabinet, never from Git.</summary>
        public bool Enabled { get; init; }

        public string BaseUrl { get; init; } = "https://api.lava.ru";

        /// <summary><c>shopId</c> — the project id in the Lava cabinet.</summary>
        public string ShopId { get; init; } = string.Empty;

        /// <summary>«Секретный ключ» of the project: signs our requests.</summary>
        public string SecretKey { get; init; } = string.Empty;

        /// <summary>«Дополнительный ключ» of the project: Lava signs its webhooks with it.</summary>
        public string WebhookKey { get; init; } = string.Empty;

        /// <summary>
        /// Payment methods on Lava's page (<c>card</c>, <c>sbp</c>, <c>sber_pay</c>, <c>mir_pay</c>, <c>mir_card</c>,
        /// <c>lava_pay_in</c>). Empty — whatever the project has switched on in the cabinet.
        /// </summary>
        public string[] IncludeServices { get; init; } = [];

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(ShopId) && !string.IsNullOrWhiteSpace(SecretKey) && !string.IsNullOrWhiteSpace(WebhookKey);
    }
}
