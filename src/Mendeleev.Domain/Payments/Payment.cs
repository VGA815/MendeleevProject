using Mendeleev.Domain.Tariffs;
using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Payments
{
    /// <summary>
    /// A payment at the aggregator (ТЗ 23). <see cref="Id"/> is our order id and is passed to the
    /// aggregator; the pair (<see cref="Provider"/>, <see cref="ProviderPaymentId"/>) is the idempotency key
    /// (FR-PAY-03). The number of days is fixed at creation, so a tariff change between creation and
    /// payment does not change what the user bought (ТЗ 22, «Правило продления»).
    /// </summary>
    public sealed class Payment : Entity
    {
        public const string Rub = "RUB";

        /// <summary>The provider of a payment taken outside the system and recorded by an admin.</summary>
        public const string ManualProvider = "manual";

        private Payment() { }

        public Guid Id { get; private set; }

        public long UserId { get; private set; }

        public int TariffId { get; private set; }

        public decimal Amount { get; private set; }

        public string Currency { get; private set; } = Rub;

        public string Provider { get; private set; } = string.Empty;

        public string? ProviderPaymentId { get; private set; }

        public PaymentStatus Status { get; private set; }

        public int DaysGranted { get; private set; }

        public string? ConfirmationUrl { get; private set; }

        /// <summary>When the aggregator's payment page stops accepting the payment, if it says so.</summary>
        public DateTime? LinkExpiresAt { get; private set; }

        public string? ReceiptId { get; private set; }

        /// <summary>Amount or currency in the notification did not match: nothing was granted, a human decides.</summary>
        public bool NeedsReview { get; private set; }

        public DateTime? LastCheckedAt { get; private set; }

        public DateTime CreatedAt { get; private set; }

        public DateTime? PaidAt { get; private set; }

        public DateTime UpdatedAt { get; private set; }

        public bool IsFinal => Status is PaymentStatus.Succeeded or PaymentStatus.Refunded;

        public static Payment Create(long userId, Tariff tariff, string provider, DateTime utcNow)
        {
            if (!tariff.IsPurchasable)
            {
                throw new InvalidOperationException($"Tariff '{tariff.Code}' cannot be bought.");
            }

            return new Payment
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                TariffId = tariff.Id,
                Amount = tariff.Price,
                Currency = Rub,
                Provider = provider,
                Status = PaymentStatus.Created,
                DaysGranted = tariff.PeriodDays,
                CreatedAt = utcNow,
                UpdatedAt = utcNow,
            };
        }

        /// <summary>
        /// Money the owner took outside the system while there is no aggregator (ТЗ 23, «Если договор с
        /// агрегатором не готов к запуску»), recorded by an admin as already paid. The amount is what was
        /// actually received: prices may not be set yet. It has no aggregator id, so the aggregator's checks and
        /// the reconciliation never touch it.
        /// </summary>
        public static Payment RecordManual(long userId, Tariff tariff, decimal amount, DateTime utcNow)
        {
            if (tariff.IsTrial)
            {
                throw new InvalidOperationException("The trial is not for sale.");
            }
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);

            return new Payment
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                TariffId = tariff.Id,
                Amount = amount,
                Currency = Rub,
                Provider = ManualProvider,
                Status = PaymentStatus.Succeeded,
                DaysGranted = tariff.PeriodDays,
                CreatedAt = utcNow,
                PaidAt = utcNow,
                UpdatedAt = utcNow,
            };
        }

        public void MarkPending(string providerPaymentId, string confirmationUrl, DateTime? linkExpiresAt, DateTime utcNow)
        {
            ProviderPaymentId = providerPaymentId;
            ConfirmationUrl = confirmationUrl;
            LinkExpiresAt = linkExpiresAt;
            Status = PaymentStatus.Pending;
            UpdatedAt = utcNow;
        }

        public void MarkCreationFailed(DateTime utcNow)
        {
            Status = PaymentStatus.Failed;
            UpdatedAt = utcNow;
        }

        /// <summary>
        /// Returns false when the payment was already applied — the caller must not grant days again.
        /// A canceled or failed payment can still succeed: a late payment found by reconciliation counts
        /// (FR-PAY-11).
        /// </summary>
        public bool TryMarkSucceeded(string? providerPaymentId, DateTime utcNow)
        {
            if (IsFinal)
            {
                return false;
            }

            ProviderPaymentId ??= providerPaymentId;
            Status = PaymentStatus.Succeeded;
            PaidAt = utcNow;
            UpdatedAt = utcNow;
            return true;
        }

        public void Cancel(DateTime utcNow)
        {
            if (Status is not (PaymentStatus.Created or PaymentStatus.Pending))
            {
                return;
            }
            Status = PaymentStatus.Canceled;
            UpdatedAt = utcNow;
        }

        public void MarkFailed(DateTime utcNow)
        {
            if (Status is not (PaymentStatus.Created or PaymentStatus.Pending))
            {
                return;
            }
            Status = PaymentStatus.Failed;
            UpdatedAt = utcNow;
        }

        public void MarkRefunded(DateTime utcNow)
        {
            if (Status != PaymentStatus.Succeeded)
            {
                return;
            }
            Status = PaymentStatus.Refunded;
            UpdatedAt = utcNow;
        }

        public void FlagForReview(DateTime utcNow)
        {
            NeedsReview = true;
            UpdatedAt = utcNow;
        }

        public void MarkChecked(DateTime utcNow)
        {
            LastCheckedAt = utcNow;
        }

        public void SetReceipt(string receiptId, DateTime utcNow)
        {
            ReceiptId = receiptId;
            UpdatedAt = utcNow;
        }

        /// <summary>
        /// An unpaid, unexpired payment for the same tariff is shown again instead of creating a new one
        /// (FR-PAY-10).
        /// </summary>
        public bool IsReusable(int tariffId, TimeSpan maxAge, DateTime utcNow) =>
            TariffId == tariffId
            && Status == PaymentStatus.Pending
            && ConfirmationUrl is not null
            && CreatedAt > utcNow - maxAge
            && (LinkExpiresAt is null || LinkExpiresAt > utcNow.AddMinutes(5));

        public bool Matches(decimal? amount, string? currency) =>
            (amount is null || amount.Value == Amount)
            && (currency is null || string.Equals(currency, Currency, StringComparison.OrdinalIgnoreCase));
    }
}
