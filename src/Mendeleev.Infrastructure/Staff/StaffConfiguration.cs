using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Staff;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mendeleev.Infrastructure.Staff
{
    internal sealed class StaffConfiguration : IEntityTypeConfiguration<StaffMember>
    {
        public void Configure(EntityTypeBuilder<StaffMember> builder)
        {
            builder.ToTable("staff");
            builder.HasKey(x => x.Id);

            builder.Property(x => x.Role).HasMaxLength(16);
            builder.Property(x => x.DisplayName).HasMaxLength(64);

            builder.HasIndex(x => x.TelegramId).IsUnique();
        }
    }

    internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLogEntry>
    {
        public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
        {
            builder.ToTable("audit_log");
            builder.HasKey(x => x.Id);

            builder.Property(x => x.ActorType).HasMaxLength(16);
            builder.Property(x => x.Action).HasMaxLength(64);
            builder.Property(x => x.Details).HasColumnType("jsonb");

            builder.HasIndex(x => new { x.TargetUserId, x.Id });
            builder.HasIndex(x => new { x.StaffId, x.Id });
            builder.HasIndex(x => new { x.Action, x.CreatedAt });
        }
    }
}
