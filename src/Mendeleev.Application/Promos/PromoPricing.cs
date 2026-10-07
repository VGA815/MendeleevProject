using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Domain.Promos;
using Mendeleev.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Promos
{
    /// <summary>
    /// One rule for the price the user sees in the bot and the cabinet and the amount the payment is created
    /// with (FR-PAY-15): the price minus the discount of the selected code, never below
    /// <c>Payments:MinAmount</c>.
    /// </summary>
    internal static class PromoPricing
    {
        public static decimal FinalPrice(decimal price, PromoCode? promo, decimal minAmount)
        {
            if (promo is not { Type: PromoType.DiscountPercent })
            {
                return price;
            }

            decimal floor = Math.Min(price, Math.Max(1m, minAmount));
            return Math.Max(floor, promo.Discount(price));
        }

        /// <summary>
        /// The discount code the user selected, if it still applies to them: active, in its window, not used up
        /// and not used by this user. <c>Stale</c> is the selected code that no longer applies.
        /// </summary>
        public static async Task<(PromoCode? Promo, PromoCode? Stale)> ResolveSelectedAsync(
            IApplicationDbContext db,
            User user,
            DateTime utcNow,
            CancellationToken cancellationToken)
        {
            if (user.SelectedPromoCodeId is not long promoCodeId)
            {
                return (null, null);
            }

            PromoCode? promo = await db.PromoCodes.AsNoTracking().FirstOrDefaultAsync(p => p.Id == promoCodeId, cancellationToken);
            if (promo is null)
            {
                return (null, null);
            }

            bool usedByUser = await db.PromoRedemptions.AnyAsync(r => r.PromoCodeId == promoCodeId && r.UserId == user.Id, cancellationToken);
            return promo.Type == PromoType.DiscountPercent && promo.CheckUsable(utcNow).IsSuccess && !usedByUser
                ? (promo, null)
                : (null, promo);
        }
    }
}
