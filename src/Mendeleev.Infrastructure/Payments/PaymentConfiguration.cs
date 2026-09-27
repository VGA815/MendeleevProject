using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mendeleev.Infrastructure.Payments
{
    internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
    {
        public void Configure(EntityTypeBuilder<Payment> builder)
        {
            builder.ToTable("payments", t =>
            {
                t.HasCheckConstraint("ck_payments_amount", "amount > 0");
                t.HasCheckConstraint("ck_payments_days_granted", "days_granted > 0");
            });
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).ValueGeneratedNever();

            builder.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            builder.Property(x => x.Provider).HasMaxLength(32);
            builder.Property(x => x.ProviderPaymentId).HasMaxLength(128);
            builder.Property(x => x.Status).HasMaxLength(16);
            builder.Property(x => x.ConfirmationUrl).HasMaxLength(2048);
            builder.Property(x => x.ReceiptId).HasMaxLength(128);

            builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne<Tariff>().WithMany().HasForeignKey(x => x.TariffId).OnDelete(DeleteBehavior.Restrict);

            // The provider's payment id is the idempotency key (FR-PAY-03).
            builder.HasIndex(x => new { x.Provider, x.ProviderPaymentId })
                .IsUnique()
                .HasFilter("provider_payment_id IS NOT NULL");
            builder.HasIndex(x => new { x.Status, x.CreatedAt });
            builder.HasIndex(x => new { x.UserId, x.CreatedAt });
        }
    }

    internal sealed class PaymentEventConfiguration : IEntityTypeConfiguration<PaymentEvent>
    {
        public void Configure(EntityTypeBuilder<PaymentEvent> builder)
        {
            builder.ToTable("payment_events");
            builder.HasKey(x => x.Id);

            builder.Property(x => x.Provider).HasMaxLength(32);
            builder.Property(x => x.Kind).HasMaxLength(32);
            builder.Property(x => x.Details).HasColumnType("jsonb");

            builder.HasIndex(x => new { x.PaymentId, x.Id });
            builder.HasIndex(x => x.CreatedAt);
        }
    }
}
