using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Payments
{
    /// <summary>
    /// Which aggregators a new payment goes to, in order (FR-PAY-14): the one an admin made active with
    /// <c>/aggregator</c>, or else <c>Payments:ActiveProvider</c>; then every other switched-on aggregator in the order
    /// of the configuration, to fall back to when the previous one refuses to create the invoice.
    /// </summary>
    internal static class PaymentProviderSelection
    {
        public static async Task<IPaymentProvider> ActiveAsync(
            IApplicationDbContext db,
            IPaymentProviderRegistry registry,
            CancellationToken cancellationToken)
        {
            string? chosen = await db.Settings
                .Where(s => s.Key == ServiceSetting.ActivePaymentProvider)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(cancellationToken);

            // A provider switched off in the configuration since then is passed over.
            return (string.IsNullOrEmpty(chosen) ? null : registry.Find(chosen)) ?? registry.Active;
        }

        public static async Task<IReadOnlyList<IPaymentProvider>> CandidatesAsync(
            IApplicationDbContext db,
            IPaymentProviderRegistry registry,
            CancellationToken cancellationToken)
        {
            IPaymentProvider active = await ActiveAsync(db, registry, cancellationToken);
            return [active, .. registry.All.Where(p => !ReferenceEquals(p, active))];
        }
    }
}
