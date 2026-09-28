using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Broadcasts;
using Mendeleev.Domain.Devices;
using Mendeleev.Domain.Notifications;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Domain.Traffic;
using Mendeleev.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Abstractions.Data
{
    public interface IApplicationDbContext
    {
        DbSet<User> Users { get; }
        DbSet<Tariff> Tariffs { get; }
        DbSet<Subscription> Subscriptions { get; }
        DbSet<Payment> Payments { get; }
        DbSet<PaymentEvent> PaymentEvents { get; }
        DbSet<StaffMember> Staff { get; }
        DbSet<AuditLogEntry> AuditLog { get; }
        DbSet<DeviceReset> DeviceResets { get; }
        DbSet<Notification> Notifications { get; }
        DbSet<Broadcast> Broadcasts { get; }
        DbSet<TrafficDaily> TrafficDaily { get; }
        DbSet<LinkCode> LinkCodes { get; }

        /// <summary>
        /// Saves the changes and, in the same transaction, the domain events raised by the tracked
        /// entities (they become outbox messages).
        /// </summary>
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

        Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Loads and row-locks the user (SELECT … FOR UPDATE) inside the current transaction. Every change
        /// of a user's subscription takes this lock first, which serializes concurrent payments, trial
        /// clicks and staff actions for one user. Payment paths lock the payment first, then the user.
        /// </summary>
        Task<User?> LockUserAsync(long userId, CancellationToken cancellationToken);

        /// <summary>Loads and row-locks a payment inside the current transaction.</summary>
        Task<Payment?> LockPaymentAsync(Guid paymentId, CancellationToken cancellationToken);

        /// <summary>
        /// INSERT … ON CONFLICT DO NOTHING by Telegram ID, then read: parallel /start from one person
        /// yields exactly one row (FR-ACC-01).
        /// </summary>
        Task<(User User, bool Created)> GetOrCreateTelegramUserAsync(long telegramId, DateTime utcNow, CancellationToken cancellationToken);

        /// <summary>Days of compensation given to the user by support staff since the given moment.</summary>
        Task<int> SumSupportCompensationDaysAsync(long userId, DateTime sinceUtc, CancellationToken cancellationToken);
    }
}
