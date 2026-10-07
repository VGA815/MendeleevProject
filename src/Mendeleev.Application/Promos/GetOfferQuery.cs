using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Configuration;
using Mendeleev.Application.Tariffs.GetPurchasableTariffs;
using Mendeleev.Domain.Promos;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mendeleev.Application.Promos
{
    /// <summary>
    /// «Купить / Продлить» for this user: the tariffs on sale with the prices the payment will really have —
    /// with the discount of the promo code the user entered, if it still applies (FR-PAY-15).
    /// </summary>
    public sealed record GetOfferQuery(long UserId) : IQuery<Offer>;

    public sealed record Offer(IReadOnlyList<OfferedTariff> Tariffs, OfferPromo? Promo)
    {
        public OfferedTariff? Find(string code) => Tariffs.FirstOrDefault(t => t.Tariff.Code == code);
    }

    /// <param name="FinalPrice">What the payment will be created with; equals the price without a code.</param>
    public sealed record OfferedTariff(TariffView Tariff, decimal FinalPrice)
    {
        public bool Discounted => FinalPrice < Tariff.Price;

        /// <summary>The final price per 30 days, for the «≈ N ₽/мес» hint (the same rounding as the tariff's own).</summary>
        public decimal MonthlyEquivalent =>
            Tariff.PeriodDays <= 30 ? FinalPrice : Math.Round(FinalPrice / Tariff.PeriodDays * 30m, 0);
    }

    public sealed record OfferPromo(string Code, int DiscountPercent, DateTime? ValidTo);

    internal sealed class GetOfferQueryHandler(
        IApplicationDbContext db,
        IDateTimeProvider clock,
        IOptions<PaymentOptions> paymentOptions)
        : IQueryHandler<GetOfferQuery, Offer>
    {
        public async Task<Result<Offer>> Handle(GetOfferQuery query, CancellationToken cancellationToken)
        {
            IReadOnlyList<TariffView> onSale = await PurchasableTariffs.LoadAsync(db, cancellationToken);

            User? user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == query.UserId, cancellationToken);
            (PromoCode? promo, _) = user is null
                ? (null, null)
                : await PromoPricing.ResolveSelectedAsync(db, user, clock.UtcNow, cancellationToken);

            decimal minAmount = paymentOptions.Value.MinAmount;
            return new Offer(
                onSale.Select(t => new OfferedTariff(t, PromoPricing.FinalPrice(t.Price, promo, minAmount))).ToList(),
                promo is null ? null : new OfferPromo(promo.Code, promo.Value, promo.ValidTo));
        }
    }
}
