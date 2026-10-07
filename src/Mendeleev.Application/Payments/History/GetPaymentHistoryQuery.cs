using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Payments;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Payments.History
{
    /// <summary>
    /// The user's own payments for the bot and the cabinet (FR-PAY-17): paid, refunded and waiting for payment,
    /// newest first. Invoices that expired unpaid or were never created are left out — they bought nothing.
    /// </summary>
    public sealed record GetPaymentHistoryQuery(long UserId) : IQuery<IReadOnlyList<PaymentHistoryItem>>;

    /// <param name="At">When it was paid, or created for one still waiting.</param>
    /// <param name="Manual">Taken outside the system and recorded by an admin.</param>
    public sealed record PaymentHistoryItem(
        Guid Id,
        DateTime At,
        string TariffName,
        int Days,
        decimal Amount,
        PaymentStatus Status,
        string? PromoCode,
        bool Manual);

    internal sealed class GetPaymentHistoryQueryHandler(IApplicationDbContext db)
        : IQueryHandler<GetPaymentHistoryQuery, IReadOnlyList<PaymentHistoryItem>>
    {
        private const int Shown = 20;

        public async Task<Result<IReadOnlyList<PaymentHistoryItem>>> Handle(GetPaymentHistoryQuery query, CancellationToken cancellationToken)
        {
            List<PaymentHistoryItem> items = await (
                    from p in db.Payments.AsNoTracking()
                    join t in db.Tariffs.AsNoTracking() on p.TariffId equals t.Id
                    join promo in db.PromoCodes.AsNoTracking() on p.PromoCodeId equals (long?)promo.Id into promos
                    from promo in promos.DefaultIfEmpty()
                    where p.UserId == query.UserId
                        && (p.Status == PaymentStatus.Succeeded || p.Status == PaymentStatus.Refunded || p.Status == PaymentStatus.Pending)
                    orderby p.CreatedAt descending
                    select new PaymentHistoryItem(
                        p.Id,
                        p.PaidAt ?? p.CreatedAt,
                        t.Name,
                        p.DaysGranted,
                        p.Amount,
                        p.Status,
                        promo != null ? promo.Code : null,
                        p.Provider == Payment.ManualProvider))
                .Take(Shown)
                .ToListAsync(cancellationToken);

            return Result.Success<IReadOnlyList<PaymentHistoryItem>>(items);
        }
    }
}
