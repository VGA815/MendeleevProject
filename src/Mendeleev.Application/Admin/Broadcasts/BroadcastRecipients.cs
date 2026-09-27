using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Domain.Broadcasts;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;

namespace Mendeleev.Application.Admin.Broadcasts
{
    /// <summary>Who a segment reaches. Blocked accounts and users who blocked the bot are never included.</summary>
    internal static class BroadcastRecipients
    {
        public static IQueryable<User> Query(IApplicationDbContext db, BroadcastSegment segment)
        {
            IQueryable<User> users = db.Users.Where(u => u.TelegramId != null && !u.BotBlocked && u.Status == UserStatus.Active);

            return segment switch
            {
                BroadcastSegment.Active => users.Where(u => db.Subscriptions.Any(s => s.UserId == u.Id && s.Status == SubscriptionStatus.Active)),
                BroadcastSegment.Trial => users.Where(u => db.Subscriptions.Any(s => s.UserId == u.Id && s.Status == SubscriptionStatus.Trial)),
                BroadcastSegment.Expired => users.Where(u => db.Subscriptions.Any(s => s.UserId == u.Id && s.Status == SubscriptionStatus.Expired)),
                BroadcastSegment.NoSubscription => users.Where(u => !db.Subscriptions.Any(s => s.UserId == u.Id && s.Status != SubscriptionStatus.Archived)),
                _ => users,
            };
        }
    }
}
