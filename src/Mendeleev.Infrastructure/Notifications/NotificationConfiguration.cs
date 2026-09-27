using Mendeleev.Domain.Broadcasts;
using Mendeleev.Domain.Devices;
using Mendeleev.Domain.Notifications;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Traffic;
using Mendeleev.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mendeleev.Infrastructure.Notifications
{
    internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
    {
        public void Configure(EntityTypeBuilder<Notification> builder)
        {
            builder.ToTable("notifications");
            builder.HasKey(x => x.Id);

            builder.Property(x => x.Kind).HasMaxLength(32);
            builder.Property(x => x.Status).HasMaxLength(16);
            builder.Property(x => x.DedupKey).HasMaxLength(200);
            builder.Property(x => x.Data).HasColumnType("jsonb");

            builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

            // Each reminder once per expiry date, even after a job restart (FR-SUB-07).
            builder.HasIndex(x => x.DedupKey).IsUnique();
            builder.HasIndex(x => x.CreatedAt);
        }
    }

    internal sealed class BroadcastConfiguration : IEntityTypeConfiguration<Broadcast>
    {
        public void Configure(EntityTypeBuilder<Broadcast> builder)
        {
            builder.ToTable("broadcasts");
            builder.HasKey(x => x.Id);

            builder.Property(x => x.Text).HasMaxLength(Broadcast.MaxTextLength);
            builder.Property(x => x.Segment).HasMaxLength(32);
            builder.Property(x => x.Status).HasMaxLength(16);

            builder.HasIndex(x => x.Status);
        }
    }

    internal sealed class DeviceResetConfiguration : IEntityTypeConfiguration<DeviceReset>
    {
        public void Configure(EntityTypeBuilder<DeviceReset> builder)
        {
            builder.ToTable("device_resets");
            builder.HasKey(x => x.Id);

            builder.Property(x => x.InitiatedBy).HasMaxLength(16);

            builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(x => new { x.UserId, x.CreatedAt });
        }
    }

    internal sealed class TrafficDailyConfiguration : IEntityTypeConfiguration<TrafficDaily>
    {
        public void Configure(EntityTypeBuilder<TrafficDaily> builder)
        {
            builder.ToTable("traffic_daily", t => t.HasCheckConstraint("ck_traffic_daily_bytes", "bytes >= 0"));
            builder.HasKey(x => new { x.SubscriptionId, x.Day });

            builder.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(x => x.Day);
        }
    }
}
