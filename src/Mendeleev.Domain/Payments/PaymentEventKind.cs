namespace Mendeleev.Domain.Payments
{
    public enum PaymentEventKind
    {
        Created = 0,
        ProviderCreated = 1,
        ProviderError = 2,
        WebhookReceived = 3,
        WebhookRejected = 4,
        WebhookUnknownPayment = 5,
        StatusChecked = 6,
        Applied = 7,
        AlreadyApplied = 8,
        AmountMismatch = 9,
        Canceled = 10,
        Failed = 11,
        Refunded = 12,
        ReconciliationMismatch = 13,
        AppliedToBlockedUser = 14,
        RecordedManually = 15,

        /// <summary>The aggregator refused to create the invoice; the payment went to the next one (FR-PAY-14).</summary>
        ProviderFallback = 16,
    }
}
