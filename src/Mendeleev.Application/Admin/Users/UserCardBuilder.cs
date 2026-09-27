using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Domain.Devices;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Admin.Users
{
    internal interface IUserCardBuilder
    {
        Task<UserCard?> BuildAsync(long userId, CancellationToken cancellationToken);
    }

    internal sealed class UserCardBuilder(IApplicationDbContext db, IPanelClient panel, IDateTimeProvider clock) : IUserCardBuilder
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

            List<PaymentCard> payments = await db.Payments
                .AsNoTracking()
                .Where(p => p.UserId == userId)
                .OrderByDescending(p => p.CreatedAt)
                .Take(5)
                .Select(p => new PaymentCard(p.Id, p.CreatedAt, p.Amount, p.Status, p.Provider, p.ProviderPaymentId, p.NeedsReview))
                .ToListAsync(cancellationToken);

            DateTime windowStart = now - DeviceReset.Window;
            int resets = await db.DeviceResets.CountAsync(r => r.UserId == userId && r.CreatedAt > windowStart, cancellationToken);

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
                resets);
        }
    }
}
