namespace Mendeleev.Infrastructure.Payments.TryBit
{
    public sealed class TryBitPaymentOptions
    {
        public const string SectionName = "Payments:TryBit";

        /// <summary>Registers the provider. The keys come from the project page in the TryBit dashboard, never from Git.</summary>
        public bool Enabled { get; init; }

        public string BaseUrl { get; init; } = "https://api.trybit.com/v2";

        /// <summary>SHOP ID — the project the invoices are issued for.</summary>
        public string ShopId { get; init; } = string.Empty;

        /// <summary>API KEY of the project: <c>Authorization: Token …</c> of every request.</summary>
        public string ApiKey { get; init; } = string.Empty;

        /// <summary>SECRET KEY of the project: TryBit signs the token of its POSTBACK with it.</summary>
        public string SecretKey { get; init; } = string.Empty;

        /// <summary>
        /// Whether invoices of a project in test mode count. Such an invoice is confirmed in the dashboard without
        /// any payment, and real money sent to it is never credited — so only Development and Staging may turn
        /// this on (FR-PAY-04); in production a test project is refused at the first payment.
        /// </summary>
        public bool AcceptTestInvoices { get; init; }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(ShopId) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(SecretKey);
    }
}
