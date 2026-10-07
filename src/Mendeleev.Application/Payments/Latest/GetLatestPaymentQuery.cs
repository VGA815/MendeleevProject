using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Payments;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Payments.Latest
{
    /// <summary>
    /// The user's latest payment with an aggregator's link in the last day. TryBit sends everyone back to one address
    /// of the project, <c>/pay/return</c> without the payment id: the cabinet shows this payment there (ТЗ 27, «Кабинет»).
    /// </summary>
    public sealed record GetLatestPaymentQuery(long UserId) : IQuery<Guid>;

    internal sealed class GetLatestPaymentQueryHandler(IApplicationDbContext db, IDateTimeProvider clock)
        : IQueryHandler<GetLatestPaymentQuery, Guid>
    {
        private static readonly TimeSpan Window = TimeSpan.FromDays(1);

        public async Task<Result<Guid>> Handle(GetLatestPaymentQuery query, CancellationToken cancellationToken)
        {
            DateTime since = clock.UtcNow - Window;

            List<Guid> latest = await db.Payments
                .AsNoTracking()
                .Where(p => p.UserId == query.UserId && p.ConfirmationUrl != null && p.CreatedAt > since)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => p.Id)
                .Take(1)
                .ToListAsync(cancellationToken);

            if (latest.Count == 0)
            {
                return PaymentErrors.NoRecentPayment;
            }

            return latest[0];
        }
    }
}
