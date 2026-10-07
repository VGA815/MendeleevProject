using System.Text.RegularExpressions;
using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Promos
{
    /// <summary>
    /// A promo code (FR-SUB-15, ТЗ 22 «Промокоды (этап 1.5)»): a discount on the next payment or bonus days.
    /// Valid within the date window, at most <see cref="MaxUses"/> times in total and once per user. The code
    /// is stored upper-case and compared without regard to case; it is Latin, so it fits the
    /// <c>/start promo_&lt;код&gt;</c> deep link (FR-BOT-20).
    /// </summary>
    /// <remarks>
    /// A discount counts only once the payment succeeds (ТЗ 22): <see cref="UsedCount"/> grows when the
    /// discounted payment is applied, so payments started at the same time may take the count a little past
    /// <see cref="MaxUses"/> — a user who paid the discounted price always gets the days. Bonus days are used
    /// at once and never go past the limit.
    /// </remarks>
    public sealed partial class PromoCode
    {
        public const int MaxDiscountPercent = 99;

        public const int MaxBonusDays = 365;

        private PromoCode() { }

        public long Id { get; private set; }

        public string Code { get; private set; } = string.Empty;

        public PromoType Type { get; private set; }

        /// <summary>Percent of the discount or number of days.</summary>
        public int Value { get; private set; }

        /// <summary>Null — no limit.</summary>
        public int? MaxUses { get; private set; }

        public int UsedCount { get; private set; }

        /// <summary>Null — from creation.</summary>
        public DateTime? ValidFrom { get; private set; }

        /// <summary>Null — until deactivated.</summary>
        public DateTime? ValidTo { get; private set; }

        public bool IsActive { get; private set; }

        public long CreatedByStaffId { get; private set; }

        public DateTime CreatedAt { get; private set; }

        public DateTime UpdatedAt { get; private set; }

        public bool IsExhausted => MaxUses is int max && UsedCount >= max;

        /// <summary>Upper-case code, or null if it cannot be a code at all (wrong length or characters).</summary>
        public static string? Normalize(string? code)
        {
            string candidate = (code ?? string.Empty).Trim().ToUpperInvariant();
            return CodeFormat().IsMatch(candidate) ? candidate : null;
        }

        public static Result<PromoCode> Create(
            string code,
            PromoType type,
            int value,
            int? maxUses,
            DateTime? validFrom,
            DateTime? validTo,
            long createdByStaffId,
            DateTime utcNow)
        {
            if (Normalize(code) is not string normalized)
            {
                return PromoErrors.InvalidCode;
            }
            if (type == PromoType.DiscountPercent && value is < 1 or > MaxDiscountPercent)
            {
                return PromoErrors.InvalidDiscount;
            }
            if (type == PromoType.BonusDays && value is < 1 or > MaxBonusDays)
            {
                return PromoErrors.InvalidBonusDays;
            }
            if (maxUses is < 1)
            {
                return PromoErrors.InvalidMaxUses;
            }
            if (validTo is DateTime to && (to <= utcNow || validFrom is DateTime from && from >= to))
            {
                return PromoErrors.InvalidWindow;
            }

            return new PromoCode
            {
                Code = normalized,
                Type = type,
                Value = value,
                MaxUses = maxUses,
                ValidFrom = validFrom,
                ValidTo = validTo,
                IsActive = true,
                CreatedByStaffId = createdByStaffId,
                CreatedAt = utcNow,
                UpdatedAt = utcNow,
            };
        }

        /// <summary>Active, inside the window and not used up — whoever asks.</summary>
        public Result CheckUsable(DateTime utcNow)
        {
            if (!IsActive)
            {
                return Result.Failure(PromoErrors.Inactive);
            }
            if (ValidFrom is DateTime from && utcNow < from)
            {
                return Result.Failure(PromoErrors.NotStarted);
            }
            if (ValidTo is DateTime to && utcNow >= to)
            {
                return Result.Failure(PromoErrors.Expired);
            }
            if (IsExhausted)
            {
                return Result.Failure(PromoErrors.Exhausted);
            }

            return Result.Success();
        }

        /// <summary>
        /// The price with the discount, in whole rubles: 199 ₽ at 15 % → 169 ₽. Never below 1 ₽ — the payment
        /// amount must stay above zero (ТЗ 23, «Бизнес-правила»).
        /// </summary>
        public decimal Discount(decimal price)
        {
            if (Type != PromoType.DiscountPercent)
            {
                throw new InvalidOperationException($"Promo code {Code} is not a discount.");
            }

            decimal discounted = Math.Round(price * (100 - Value) / 100m, 0, MidpointRounding.AwayFromZero);
            return Math.Max(1m, discounted);
        }

        /// <summary>One more use. Called under the code's row lock, so concurrent uses are counted one by one.</summary>
        public void RegisterUse(DateTime utcNow)
        {
            UsedCount++;
            UpdatedAt = utcNow;
        }

        public Result Deactivate(DateTime utcNow)
        {
            if (!IsActive)
            {
                return Result.Failure(PromoErrors.AlreadyDeactivated);
            }

            IsActive = false;
            UpdatedAt = utcNow;
            return Result.Success();
        }

        [GeneratedRegex("^[A-Z0-9_-]{3,32}$", RegexOptions.CultureInvariant)]
        private static partial Regex CodeFormat();
    }
}
