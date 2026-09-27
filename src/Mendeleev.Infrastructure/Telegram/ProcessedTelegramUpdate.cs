using Mendeleev.Application.Abstractions.Telegram;
using Mendeleev.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mendeleev.SharedKernel;

namespace Mendeleev.Infrastructure.Telegram
{
    /// <summary>An <c>update_id</c> already taken into work. Kept for 24 hours (ТЗ 26, «Бизнес-правила»).</summary>
    internal sealed class ProcessedTelegramUpdate
    {
        public long UpdateId { get; set; }

        public DateTime ReceivedAt { get; set; }
    }

    internal sealed class ProcessedTelegramUpdateConfiguration : IEntityTypeConfiguration<ProcessedTelegramUpdate>
    {
        public void Configure(EntityTypeBuilder<ProcessedTelegramUpdate> builder)
        {
            builder.ToTable("processed_telegram_updates");
            builder.HasKey(x => x.UpdateId);
            builder.Property(x => x.UpdateId).ValueGeneratedNever();
            builder.HasIndex(x => x.ReceivedAt);
        }
    }

    internal sealed class TelegramUpdateDeduplicator(ApplicationDbContext db, IDateTimeProvider clock) : ITelegramUpdateDeduplicator
    {
        public async Task<bool> TryRegisterAsync(long updateId, CancellationToken cancellationToken)
        {
            int inserted = await db.Database.ExecuteSqlAsync(
                $"INSERT INTO processed_telegram_updates (update_id, received_at) VALUES ({updateId}, {clock.UtcNow}) ON CONFLICT (update_id) DO NOTHING",
                cancellationToken);
            return inserted == 1;
        }
    }
}
