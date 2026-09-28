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

            // The default covers the raw INSERT of GetOrCreateTelegramUserAsync.
            builder.Property(x => x.SessionStamp).HasDefaultValueSql("gen_random_uuid()");

            // Partial unique indexes: many rows have no Telegram ID, email or key (ТЗ 12).
            builder.HasIndex(x => x.TelegramId).IsUnique().HasFilter("telegram_id IS NOT NULL");
            builder.HasIndex(x => x.Email).IsUnique().HasFilter("email IS NOT NULL");
            builder.HasIndex(x => x.AccountKeyHash).IsUnique().HasFilter("account_key_hash IS NOT NULL");
            builder.HasIndex(x => x.CreatedAt);
        }
    }

    internal sealed class LinkCodeConfiguration : IEntityTypeConfiguration<LinkCode>
    {
        public void Configure(EntityTypeBuilder<LinkCode> builder)
        {
            builder.ToTable("link_codes");
            builder.HasKey(x => x.Id);

            builder.Property(x => x.Purpose).HasMaxLength(16);
            builder.Property(x => x.CodeHash).HasMaxLength(64);

            // Deleting an account (merge, ТЗ 21) takes its codes with it.
            builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

            // Not unique: used and expired codes stay until the daily clean-up.
            builder.HasIndex(x => x.CodeHash);
            builder.HasIndex(x => x.UserId);
            builder.HasIndex(x => x.ExpiresAt);
        }
    }
}
