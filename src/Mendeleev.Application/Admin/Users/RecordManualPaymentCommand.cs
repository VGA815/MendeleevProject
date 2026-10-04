using FluentValidation;
using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Admin.Users
{
    /// <summary>
    /// A payment the owner took outside the system (ТЗ 23, «Если договор с агрегатором не готов к запуску»):
    /// an admin records it and the user gets the tariff as if it was bought in the bot — the term by FR-SUB-04,
    /// a trial becomes an active subscription without the trial's traffic limit (FR-SUB-05), an archived one
    /// gets a new link (FR-SUB-09). It is kept as a payment of the provider <c>manual</c>, so sales statistics
    /// and the user card show it. Admins only; the comment says how and when the money came.
    /// </summary>
    public sealed record RecordManualPaymentCommand(long StaffId, long UserId, string TariffCode, decimal Amount, string Comment)
        : ICommand<ManualPaymentRecorded>;

    public sealed record ManualPaymentRecorded(Guid PaymentId, DateTime ExpiresAt);

    internal sealed class RecordManualPaymentCommandValidator : AbstractValidator<RecordManualPaymentCommand>
    {
        public RecordManualPaymentCommandValidator()
        {
            RuleFor(x => x.Amount)
                .Must(a => a is >= 1m and <= 100_000m && a == decimal.Truncate(a))
                .WithMessage("Сумма — целое число рублей от 1 до 100 000.");
            RuleFor(x => x.Comment)
                .Must(c => c?.Trim().Length is >= 3 and <= 500)
                .WithMessage("Напишите комментарий: как и когда получена оплата (от 3 до 500 символов).");
        }
    }

    internal sealed class RecordManualPaymentCommandHandler(
        IApplicationDbContext db,
        IStaffAuthorizer authorizer,
        IDateTimeProvider clock)
        : ICommandHandler<RecordManualPaymentCommand, ManualPaymentRecorded>
    {
        public async Task<Result<ManualPaymentRecorded>> Handle(RecordManualPaymentCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.RecordManualPayments, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            // Any paid tariff, active or not: while there is no aggregator, sales in the bot stay closed.
            Tariff? tariff = await db.Tariffs.FirstOrDefaultAsync(t => t.Code == command.TariffCode, cancellationToken);
            if (tariff is null)
            {
                return TariffErrors.NotFound(command.TariffCode);
            }
            if (tariff.IsTrial)
            {
                return TariffErrors.TrialNotForSale;
            }

            DateTime now = clock.UtcNow;
            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);

            User? user = await db.LockUserAsync(command.UserId, cancellationToken);
            if (user is null)
            {
                return UserErrors.NotFound(command.UserId);
            }
            if (user.IsBlocked)
            {
                return PaymentErrors.ManualForBlockedUser;
            }

            Payment payment = Payment.RecordManual(user.Id, tariff, command.Amount, now);
            db.Payments.Add(payment);

            Subscription? subscription = await db.Subscriptions
                .Include(s => s.Tariff)
                .FirstOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);
            DateTime? before = subscription?.ExpiresAt;

            if (subscription is null)
            {
                subscription = Subscription.CreatePaid(user.Id, tariff, payment.DaysGranted, payment.Id, now);
                db.Subscriptions.Add(subscription);
            }
            else
            {
                subscription.ApplyPayment(tariff, payment.DaysGranted, payment.Id, now);
            }

            db.PaymentEvents.Add(PaymentEvent.Create(
                payment.Id,
                payment.Provider,
                PaymentEventKind.RecordedManually,
                Json.Serialize(new { staffId = staff.Value.Id }),
                now));
            db.AuditLog.Add(AuditLogEntry.ByStaff(
                staff.Value.Id,
                AuditActions.PaymentManual,
                user.Id,
                Json.Serialize(new
                {
                    paymentId = payment.Id,
                    tariff = tariff.Code,
                    days = payment.DaysGranted,
                    amount = payment.Amount,
                    comment = command.Comment.Trim(),
                    before,
                    after = subscription.ExpiresAt,
                }),
                now));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            AppMetrics.Payments.Add(1,
                new KeyValuePair<string, object?>("status", "succeeded"),
                new KeyValuePair<string, object?>("provider", payment.Provider));

            return new ManualPaymentRecorded(payment.Id, subscription.ExpiresAt);
        }
    }
}
