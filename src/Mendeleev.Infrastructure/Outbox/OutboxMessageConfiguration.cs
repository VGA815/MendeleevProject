using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mendeleev.Infrastructure.Outbox
{
    internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
    {
        public void Configure(EntityTypeBuilder<OutboxMessage> builder)
        {
            builder.ToTable("outbox_messages");
            builder.HasKey(x => x.Id);

            builder.Property(x => x.Type).HasMaxLength(128);
            builder.Property(x => x.Payload).HasColumnType("jsonb");
            builder.Property(x => x.Status).HasMaxLength(16).HasConversion<Database.SnakeCaseEnumConverter<OutboxStatus>>();

            builder.HasIndex(x => new { x.Status, x.NextAttemptAt });
            builder.HasIndex(x => x.ProcessedAt);
        }
    }
}
