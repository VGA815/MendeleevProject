using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Application.Admin.Anomalies;
using Mendeleev.Application.Configuration;
using Mendeleev.Domain.Devices;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mendeleev.Application.Admin.Users
{
    internal interface IUserCardBuilder
    {
        Task<UserCard?> BuildAsync(long userId, CancellationToken cancellationToken);
    }

    internal sealed class UserCardBuilder(IApplicationDbContext db, IPanelClient panel, IDateTimeProvider clock, IOptions<AnomalyOptions> anomalyOptions)
        : IUserCardBuilder
    {
        public async Task<UserCard?> BuildAsync(long userId, CancellationToken cancellationToken)
        {
            User? user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
            {
                return null;
            }

            DateTime now = clock.UtcNow;
            Subscription? subscription = await db.Subscriptions
                .AsNoTracking()
                .Include(s => s.Tariff)
                .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);

            List<PaymentCard> payments = await (
                    from p in db.Payments.AsNoTracking()
                    where p.UserId == userId
                    join promo in db.PromoCodes.AsNoTracking() on p.PromoCodeId equals (long?)promo.Id into promos
                    from promo in promos.DefaultIfEmpty()
                    orderby p.CreatedAt descending
                    select new PaymentCard(p.Id, p.CreatedAt, p.Amount, p.Status, p.Provider, p.ProviderPaymentId, p.NeedsReview, promo != null ? promo.Code : null))
                .Take(5)
                .ToListAsync(cancellationToken);

            DateTime windowStart = now - DeviceReset.Window;
            int resets = await db.DeviceResets.CountAsync(r => r.UserId == userId && r.CreatedAt > windowStart, cancellationToken);
            UserAnomalies anomalies = await AnomalyDetector.ForUserAsync(db, anomalyOptions.Value, userId, subscription?.Id, resets, now, cancellationToken);

            DevicesCard? devices = null;
            if (subscription?.PanelUserId is int panelUserId)
            {
                try
                {
                    IReadOnlyList<PanelDevice> items = await panel.GetDevicesAsync(panelUserId, cancellationToken);
                    devices = new DevicesCard(true, subscription.Tariff.DeviceLimit, items.OrderBy(d => d.CreatedAt).ToList());
                }
                catch (PanelException)
                {
                    devices = new DevicesCard(false, subscription.Tariff.DeviceLimit, []);
                }
            }

            SubscriptionCard? subscriptionCard = subscription is null
                ? null
                : new SubscriptionCard(
                    subscription.Tariff.Name,
                    subscription.Status,
                    subscription.ExpiresAt,
                    subscription.DaysLeft(now),
                    subscription.FirstConnectedAt,
                    subscription.SyncState,
                    subscription.UpdatedAt,
                    subscription.SubscriptionUrl is not null);

            return new UserCard(
                user.Id,
                user.TelegramId,
                user.Status,
                user.CreatedAt,
                user.TrialUsed,
                user.BotBlocked,
                user.AccountKeyHash is not null,
                subscriptionCard,
                payments,
                devices,
                resets,
                anomalies);
        }
    }
}
