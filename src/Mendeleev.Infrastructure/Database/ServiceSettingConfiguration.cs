using Mendeleev.Domain.Common;
using Mendeleev.Domain.Staff;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mendeleev.Infrastructure.Database
{
    internal sealed class ServiceSettingConfiguration : IEntityTypeConfiguration<ServiceSetting>
    {
        public void Configure(EntityTypeBuilder<ServiceSetting> builder)
        {
            builder.ToTable("service_settings");
            builder.HasKey(x => x.Key);

            builder.Property(x => x.Key).HasMaxLength(64);
            builder.Property(x => x.Value).HasMaxLength(256);

            builder.HasOne<StaffMember>().WithMany().HasForeignKey(x => x.UpdatedByStaffId).OnDelete(DeleteBehavior.SetNull);
        }
    }
}
