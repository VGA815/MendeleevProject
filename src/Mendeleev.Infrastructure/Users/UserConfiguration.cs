using Mendeleev.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mendeleev.Infrastructure.Users
{
    internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> builder)
        {
            builder.ToTable("users");
            builder.HasKey(x => x.Id);

            builder.Property(x => x.Email).HasColumnType("citext").HasMaxLength(254);
            builder.Property(x => x.AccountKeyHash).HasMaxLength(64);
            builder.Property(x => x.Status).HasMaxLength(16);

            // Partial unique indexes: many rows have no Telegram ID, email or key (ТЗ 12).
            builder.HasIndex(x => x.TelegramId).IsUnique().HasFilter("telegram_id IS NOT NULL");
            builder.HasIndex(x => x.Email).IsUnique().HasFilter("email IS NOT NULL");
            builder.HasIndex(x => x.AccountKeyHash).IsUnique().HasFilter("account_key_hash IS NOT NULL");
            builder.HasIndex(x => x.CreatedAt);
        }
    }
}
