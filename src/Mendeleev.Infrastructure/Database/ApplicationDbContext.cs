using Mendeleev.Application.Abstractions.Data;
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
using Mendeleev.Infrastructure.Outbox;
using Mendeleev.Infrastructure.Telegram;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Infrastructure.Database
{
    public sealed class ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        IDateTimeProvider clock,
        OutboxSignal outboxSignal)
        : DbContext(options), IApplicationDbContext
    {
        public DbSet<User> Users { get; set; }

        public DbSet<Tariff> Tariffs { get; set; }

        public DbSet<Subscription> Subscriptions { get; set; }

        public DbSet<Payment> Payments { get; set; }

        public DbSet<PaymentEvent> PaymentEvents { get; set; }

        public DbSet<StaffMember> Staff { get; set; }

        public DbSet<AuditLogEntry> AuditLog { get; set; }

        public DbSet<DeviceReset> DeviceResets { get; set; }

        public DbSet<Notification> Notifications { get; set; }

        public DbSet<Broadcast> Broadcasts { get; set; }

        public DbSet<TrafficDaily> TrafficDaily { get; set; }

        public DbSet<LinkCode> LinkCodes { get; set; }

        internal DbSet<OutboxMessage> OutboxMessages { get; set; }

        internal DbSet<ProcessedTelegramUpdate> ProcessedTelegramUpdates { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema(Schemas.Default);
            modelBuilder.HasPostgresExtension("citext");
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
        }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            // Enums are stored as snake_case text: readable in SQL and in dumps (ТЗ 12: "status text").
            foreach (Type enumType in typeof(User).Assembly.GetTypes().Where(t => t.IsEnum))
            {
                Type converter = typeof(SnakeCaseEnumConverter<>).MakeGenericType(enumType);
                configurationBuilder.Properties(enumType).HaveConversion(converter);
                configurationBuilder.Properties(typeof(Nullable<>).MakeGenericType(enumType)).HaveConversion(converter);
            }

            configurationBuilder.Properties<decimal>().HavePrecision(10, 2);
        }

        /// <summary>
        /// Domain events raised by the tracked entities become outbox rows in the same transaction as the
        /// change itself (ТЗ 10, «Transactional outbox»): an extension is never saved without its panel
        /// sync, and a message never goes out for a change that was rolled back.
        /// </summary>
        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            List<IDomainEvent> events = ChangeTracker
                .Entries<Entity>()
                .SelectMany(entry =>
                {
                    List<IDomainEvent> raised = entry.Entity.DomainEvents;
                    entry.Entity.ClearDomainEvents();
                    return raised;
                })
                .ToList();

            DateTime now = clock.UtcNow;
            foreach (IDomainEvent domainEvent in events)
            {
                OutboxMessages.Add(OutboxMessage.FromDomainEvent(domainEvent, now));
            }

            int result = await base.SaveChangesAsync(cancellationToken);

            if (events.Count > 0)
            {
                outboxSignal.Notify();
            }

            return result;
        }

        public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            Database.BeginTransactionAsync(cancellationToken);

        public async Task<User?> LockUserAsync(long userId, CancellationToken cancellationToken)
        {
            EnsureTransaction();
            bool wasTracked = IsTracked<User>(u => u.Id == userId);

            User? user = await Users
                .FromSql($"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
                .FirstOrDefaultAsync(cancellationToken);

            // A tracked instance is returned as is by the query; refresh it now that the row is ours.
            if (user is not null && wasTracked)
            {
                await Entry(user).ReloadAsync(cancellationToken);
            }

            return user;
        }

        public async Task<Payment?> LockPaymentAsync(Guid paymentId, CancellationToken cancellationToken)
        {
            EnsureTransaction();
            bool wasTracked = IsTracked<Payment>(p => p.Id == paymentId);

            Payment? payment = await Payments
                .FromSql($"SELECT * FROM payments WHERE id = {paymentId} FOR UPDATE")
                .FirstOrDefaultAsync(cancellationToken);

            if (payment is not null && wasTracked)
            {
                await Entry(payment).ReloadAsync(cancellationToken);
            }

            return payment;
        }

        public async Task<(User User, bool Created)> GetOrCreateTelegramUserAsync(long telegramId, DateTime utcNow, CancellationToken cancellationToken)
        {
            User? existing = await Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId, cancellationToken);
            if (existing is not null)
            {
                return (existing, false);
            }

            int inserted = await Database.ExecuteSqlAsync(
                $"""
                INSERT INTO users (telegram_id, status, trial_used, bot_blocked, created_at, updated_at)
                VALUES ({telegramId}, 'active', FALSE, FALSE, {utcNow}, {utcNow})
                ON CONFLICT (telegram_id) WHERE telegram_id IS NOT NULL DO NOTHING
                """,
                cancellationToken);

            User user = await Users.FirstAsync(u => u.TelegramId == telegramId, cancellationToken);
            return (user, inserted == 1);
        }

        public async Task<int> SumSupportCompensationDaysAsync(long userId, DateTime sinceUtc, CancellationToken cancellationToken)
        {
            return await Database
                .SqlQuery<int>(
                    $"""
                    SELECT COALESCE(SUM((details ->> 'days')::int), 0) AS "Value"
                    FROM audit_log
                    WHERE action = {AuditActions.SubscriptionExtend}
                      AND target_user_id = {userId}
                      AND created_at > {sinceUtc}
                      AND details ->> 'role' = {StaffRole.Support.ToString()}
                    """)
                .SingleAsync(cancellationToken);
        }

        private bool IsTracked<TEntity>(Func<TEntity, bool> predicate)
            where TEntity : class =>
            ChangeTracker.Entries<TEntity>().Any(e => predicate(e.Entity));

        private void EnsureTransaction()
        {
            if (Database.CurrentTransaction is null)
            {
                throw new InvalidOperationException("A row lock only makes sense inside a transaction.");
            }
        }
    }
}
