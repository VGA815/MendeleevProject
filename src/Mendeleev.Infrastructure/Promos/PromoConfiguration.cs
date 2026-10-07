using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Promos;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mendeleev.Infrastructure.Promos
{
    internal sealed class PromoCodeConfiguration : IEntityTypeConfiguration<PromoCode>
    {
        public void Configure(EntityTypeBuilder<PromoCode> builder)
        {
            builder.ToTable("promo_codes", t =>
            {
                t.HasCheckConstraint("ck_promo_codes_value", "value > 0");
                t.HasCheckConstraint("ck_promo_codes_max_uses", "max_uses IS NULL OR max_uses > 0");
                t.HasCheckConstraint("ck_promo_codes_used_count", "used_count >= 0");
            });
            builder.HasKey(x => x.Id);

            // citext: one code whatever the case (ТЗ 12); the service also stores it upper-case.
            builder.Property(x => x.Code).HasColumnType("citext").HasMaxLength(32);
            builder.Property(x => x.Type).HasMaxLength(32);

            builder.HasOne<StaffMember>().WithMany().HasForeignKey(x => x.CreatedByStaffId).OnDelete(DeleteBehavior.Restrict);

            builder.HasIndex(x => x.Code).IsUnique();
        }
    }

    internal sealed class PromoRedemptionConfiguration : IEntityTypeConfiguration<PromoRedemption>
    {
        public void Configure(EntityTypeBuilder<PromoRedemption> builder)
        {
            builder.ToTable("promo_redemptions");
            builder.HasKey(x => x.Id);

            builder.HasOne<PromoCode>().WithMany().HasForeignKey(x => x.PromoCodeId).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);

            // One code once per user (ТЗ 12, «PromoRedemption»).
            builder.HasIndex(x => new { x.PromoCodeId, x.UserId }).IsUnique();
            builder.HasIndex(x => x.UserId);
        }
    }
}
