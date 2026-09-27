using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Common;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Admin.Stats
{
    /// <summary>Sales statistics for today, 7 and 30 days (FR-ADM-09, ТЗ 28, «Статистика»).</summary>
    public sealed record GetStatsQuery(long StaffId, StatsPeriod Period) : IQuery<SalesStats>;

    public enum StatsPeriod
    {
        Today,
        Yesterday,
        Last7Days,
        Last30Days,
    }

    public sealed record SalesStats(
        StatsPeriod Period,
        DateTime FromUtc,
        DateTime ToUtc,
        int NewUsers,
        int TrialsIssued,
        int TrialsConverted,
        IReadOnlyList<TariffSales> Sales,
        int ActiveTrials,
        int ActivePaid,
        int ExpiringIn7Days,
        int NotRenewed,
        int Refunds)
    {
        public int PaymentsCount => Sales.Sum(s => s.Count);

        public decimal PaymentsSum => Sales.Sum(s => s.Sum);

        public double TrialConversion => TrialsIssued == 0 ? 0 : (double)TrialsConverted / TrialsIssued;
    }

    public sealed record TariffSales(string TariffName, int Count, decimal Sum);

    internal sealed class GetStatsQueryHandler(
        IApplicationDbContext db,
        IStaffAuthorizer authorizer,
        IDateTimeProvider clock)
        : IQueryHandler<GetStatsQuery, SalesStats>
    {
        public async Task<Result<SalesStats>> Handle(GetStatsQuery query, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(query.StaffId, StaffPermission.ViewStats, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            return await StatsCalculator.CalculateAsync(db, query.Period, clock.UtcNow, cancellationToken);
        }
    }

    internal static class StatsCalculator
    {
        public static async Task<SalesStats> CalculateAsync(IApplicationDbContext db, StatsPeriod period, DateTime now, CancellationToken cancellationToken)
        {
            DateOnly today = MoscowTime.Today(now);
            (DateTime from, DateTime to) = period switch
            {
                StatsPeriod.Today => (MoscowTime.StartOfDayUtc(today), now),
                StatsPeriod.Yesterday => (MoscowTime.StartOfDayUtc(today.AddDays(-1)), MoscowTime.StartOfDayUtc(today)),
                StatsPeriod.Last7Days => (now.AddDays(-7), now),
                _ => (now.AddDays(-30), now),
            };

            int newUsers = await db.Users.CountAsync(u => u.CreatedAt >= from && u.CreatedAt < to, cancellationToken);

            List<long> trialUsers = await db.Users
                .Where(u => u.TrialStartedAt >= from && u.TrialStartedAt < to)
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);

            int converted = trialUsers.Count == 0 ? 0 : await db.Payments
                .Where(p => trialUsers.Contains(p.UserId) && p.Status == PaymentStatus.Succeeded)
                .Select(p => p.UserId)
                .Distinct()
                .CountAsync(cancellationToken);

            var sales = await db.Payments
                .Where(p => p.Status == PaymentStatus.Succeeded && p.PaidAt >= from && p.PaidAt < to)
                .Join(db.Tariffs, p => p.TariffId, t => t.Id, (p, t) => new { t.Name, p.Amount })
                .GroupBy(x => x.Name)
                .Select(g => new TariffSales(g.Key, g.Count(), g.Sum(x => x.Amount)))
                .ToListAsync(cancellationToken);

            int activeTrials = await db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Trial, cancellationToken);
            int activePaid = await db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Active, cancellationToken);

            DateTime weekAhead = now.AddDays(7);
            int expiring = await db.Subscriptions.CountAsync(
                s => (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trial) && s.ExpiresAt <= weekAhead,
                cancellationToken);

            int notRenewed = await db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Expired, cancellationToken);

            int refunds = await db.Payments.CountAsync(
                p => p.Status == PaymentStatus.Refunded && p.UpdatedAt >= from && p.UpdatedAt < to,
                cancellationToken);

            return new SalesStats(
                period, from, to, newUsers, trialUsers.Count, converted, sales,
                activeTrials, activePaid, expiring, notRenewed, refunds);
        }
    }
}
