namespace Mendeleev.Domain.Payments
{
    /// <summary>
    /// Payment journal for disputed cases (FR-PAY-13): creation, notifications, decisions, errors. Never
    /// contains card data — the service does not receive it (FR-PAY-05).
    /// </summary>
    public sealed class PaymentEvent
    {
        private PaymentEvent() { }

        public long Id { get; private set; }

        public Guid? PaymentId { get; private set; }

        public string Provider { get; private set; } = string.Empty;

        public PaymentEventKind Kind { get; private set; }

        /// <summary>JSON with the details of the event.</summary>
        public string? Details { get; private set; }

        public DateTime CreatedAt { get; private set; }

        public static PaymentEvent Create(Guid? paymentId, string provider, PaymentEventKind kind, string? details, DateTime utcNow) => new()
        {
            PaymentId = paymentId,
            Provider = provider,
            Kind = kind,
            Details = details,
            CreatedAt = utcNow,
        };
    }
}
