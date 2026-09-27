using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mendeleev.Infrastructure.Subscriptions
{
    internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
    {
        public void Configure(EntityTypeBuilder<Subscription> builder)
        {
            builder.ToTable("subscriptions");
            builder.HasKey(x => x.Id);

            builder.Property(x => x.Status).HasMaxLength(16);
            builder.Property(x => x.ExpiredReason).HasMaxLength(16);
            builder.Property(x => x.SyncState).HasMaxLength(16);
            builder.Property(x => x.PanelShortUuid).HasMaxLength(64);
            builder.Property(x => x.SubscriptionUrl).HasMaxLength(512);

            builder.HasOne<User>()
                .WithOne()
                .HasForeignKey<Subscription>(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(x => x.Tariff)
                .WithMany()
                .HasForeignKey(x => x.TariffId)
                .OnDelete(DeleteBehavior.Restrict);

            // One subscription per user (FR-SUB-03).
            builder.HasIndex(x => x.UserId).IsUnique();
            // The short UUID is the secret part of the link: never shared by two users.
            builder.HasIndex(x => x.PanelShortUuid).IsUnique();
            builder.HasIndex(x => x.PanelUserId).IsUnique().HasFilter("panel_user_id IS NOT NULL");
            // Background jobs scan by status and term.
            builder.HasIndex(x => new { x.Status, x.ExpiresAt });
        }
    }
}
