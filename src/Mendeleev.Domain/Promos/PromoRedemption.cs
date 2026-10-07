namespace Mendeleev.Domain.Promos
{
    /// <summary>
    /// A promo code used by a user (ТЗ 12, «PromoRedemption»): a discounted payment that succeeded, or bonus
    /// days already given. Unique by (code, user) — one use per user.
    /// </summary>
    public sealed class PromoRedemption
    {
        private PromoRedemption() { }

        public long Id { get; private set; }

        public long PromoCodeId { get; private set; }

        public long UserId { get; private set; }

        /// <summary>The discounted payment; null for bonus days.</summary>
        public Guid? PaymentId { get; private set; }

        /// <summary>Days given by a bonus code; null for a discount.</summary>
        public int? BonusDays { get; private set; }

        public DateTime CreatedAt { get; private set; }

        public static PromoRedemption ForPayment(long promoCodeId, long userId, Guid paymentId, DateTime utcNow) => new()
        {
            PromoCodeId = promoCodeId,
            UserId = userId,
            PaymentId = paymentId,
            CreatedAt = utcNow,
        };

        public static PromoRedemption ForBonus(long promoCodeId, long userId, int days, DateTime utcNow) => new()
        {
            PromoCodeId = promoCodeId,
            UserId = userId,
            BonusDays = days,
            CreatedAt = utcNow,
        };
    }
}
