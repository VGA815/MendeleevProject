namespace Mendeleev.Domain.Promos
{
    /// <summary>ТЗ 22, «Промокоды (этап 1.5)».</summary>
    public enum PromoType
    {
        /// <summary>A discount of 1–99 % on the next payment.</summary>
        DiscountPercent = 0,

        /// <summary>Days of access without a payment.</summary>
        BonusDays = 1,
    }
}
