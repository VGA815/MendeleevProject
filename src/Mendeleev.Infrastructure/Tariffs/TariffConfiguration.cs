using Mendeleev.Domain.Tariffs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mendeleev.Infrastructure.Tariffs
{
    internal sealed class TariffConfiguration : IEntityTypeConfiguration<Tariff>
    {
        public void Configure(EntityTypeBuilder<Tariff> builder)
        {
            builder.ToTable("tariffs", t =>
            {
                t.HasCheckConstraint("ck_tariffs_price", "price >= 0");
                t.HasCheckConstraint("ck_tariffs_period_days", "period_days > 0");
                t.HasCheckConstraint("ck_tariffs_device_limit", "device_limit > 0");
            });
            builder.HasKey(x => x.Id);

            builder.Property(x => x.Code).HasMaxLength(32);
            builder.Property(x => x.Name).HasMaxLength(100);
            builder.Property(x => x.Tier).HasMaxLength(16);
            builder.Property(x => x.PanelSquads).HasColumnType("uuid[]");

            builder.HasIndex(x => x.Code).IsUnique();
        }
    }
}
