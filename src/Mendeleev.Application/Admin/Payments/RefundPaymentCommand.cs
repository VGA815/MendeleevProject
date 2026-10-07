using FluentValidation;
using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Payments;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Tariffs;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Admin.Payments
{
    /// <summary>
    /// An admin marks a payment refunded once the money went back (FR-PAY-16, FR-ADM-18). What happens to the
    /// subscription follows the offer (решение 07.10): unused days returned — access ends now (п. 6.3); an
    /// erroneous or duplicate payment returned in full — the term loses that payment's days, not past now (п. 6.2).
    /// The user is told; the mark and the term before and after go to the audit. Locks: payment → user.
    /// </summary>
    public sealed record RefundPaymentCommand(long StaffId, Guid PaymentId, RefundKind Kind, string Reason) : ICommand<RefundResult>;

    /// <param name="ExpiresAt">The end of the subscription after the refund; null if the user has none.</param>
    public sealed record RefundResult(long UserId, DateTime? ExpiresAt, bool AccessEnded);

    /// <summary>Payments of a user that can be marked refunded, newest first — the choice in the bot.</summary>
    public sealed record ListRefundablePaymentsQuery(long StaffId, long UserId) : IQuery<IReadOnlyList<RefundablePayment>>;

    /// <summary>One payment for the refund screen, by our id.</summary>
    public sealed record GetRefundablePaymentQuery(long StaffId, Guid PaymentId) : IQuery<RefundablePayment>;

    public sealed record RefundablePayment(
        Guid Id,
        long UserId,
        DateTime PaidAt,
        decimal Amount,
        string Provider,
        string TariffName,
        int Days,
        PaymentStatus Status);

    internal sealed class RefundPaymentCommandValidator : AbstractValidator<RefundPaymentCommand>
    {
        public RefundPaymentCommandValidator()
        {
            RuleFor(x => x.Reason).Must(r => r?.Trim().Length is >= 3 and <= 500).WithMessage(StaffErrors.ReasonRequired.Description);
            RuleFor(x => x.Kind).IsInEnum();
        }
    }

    internal sealed class RefundPaymentCommandHandler(
        IApplicationDbContext db,
        IStaffAuthorizer authorizer,
        IDateTimeProvider clock)
        : ICommandHandler<RefundPaymentCommand, RefundResult>
    {
        public async Task<Result<RefundResult>> Handle(RefundPaymentCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.RefundPayments, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            DateTime now = clock.UtcNow;
            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);

            Payment? payment = await db.LockPaymentAsync(command.PaymentId, cancellationToken);
            if (payment is null)
            {
                return PaymentErrors.NotFound(command.PaymentId);
            }

            Result refunded = payment.Refund(now);
            if (refunded.IsFailure)
            {
                return refunded.Error;
            }

            User user = await db.LockUserAsync(payment.UserId, cancellationToken)
                ?? throw new InvalidOperationException($"Payment {payment.Id} references missing user {payment.UserId}.");

            Subscription? subscription = await db.Subscriptions
                .Include(s => s.Tariff)
                .FirstOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);

            DateTime? before = subscription?.ExpiresAt;
            subscription?.ApplyRefund(
                command.Kind == RefundKind.Erroneous ? payment.DaysGranted : null,
                payment.Id.ToString("N"),
                now);

            db.PaymentEvents.Add(PaymentEvent.Create(
                payment.Id,
                payment.Provider,
                PaymentEventKind.Refunded,
                Json.Serialize(new { staffId = staff.Value.Id, kind = command.Kind.ToString() }),
                now));
            db.AuditLog.Add(AuditLogEntry.ByStaff(
                staff.Value.Id,
                AuditActions.PaymentRefund,
                user.Id,
                Json.Serialize(new
                {
                    paymentId = payment.Id,
                    amount = payment.Amount,
                    days = payment.DaysGranted,
                    kind = command.Kind.ToString(),
                    reason = command.Reason.Trim(),
                    before,
                    after = subscription?.ExpiresAt,
                }),
                now));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new RefundResult(user.Id, subscription?.ExpiresAt, subscription is not null && subscription.ExpiresAt <= now);
        }
    }

    internal sealed class ListRefundablePaymentsQueryHandler(IApplicationDbContext db, IStaffAuthorizer authorizer)
        : IQueryHandler<ListRefundablePaymentsQuery, IReadOnlyList<RefundablePayment>>
    {
        private const int Shown = 10;

        public async Task<Result<IReadOnlyList<RefundablePayment>>> Handle(ListRefundablePaymentsQuery query, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(query.StaffId, StaffPermission.RefundPayments, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            List<RefundablePayment> payments = await (
                    from p in db.Payments.AsNoTracking()
                    join t in db.Tariffs.AsNoTracking() on p.TariffId equals t.Id
                    where p.UserId == query.UserId && p.Status == PaymentStatus.Succeeded && p.PaidAt != null
                    orderby p.PaidAt descending
                    select new RefundablePayment(p.Id, p.UserId, p.PaidAt!.Value, p.Amount, p.Provider, t.Name, p.DaysGranted, p.Status))
                .Take(Shown)
                .ToListAsync(cancellationToken);

            return Result.Success<IReadOnlyList<RefundablePayment>>(payments);
        }
    }

    internal sealed class GetRefundablePaymentQueryHandler(IApplicationDbContext db, IStaffAuthorizer authorizer)
        : IQueryHandler<GetRefundablePaymentQuery, RefundablePayment>
    {
        public async Task<Result<RefundablePayment>> Handle(GetRefundablePaymentQuery query, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(query.StaffId, StaffPermission.RefundPayments, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            RefundablePayment? payment = await (
                    from p in db.Payments.AsNoTracking()
                    join t in db.Tariffs.AsNoTracking() on p.TariffId equals t.Id
                    where p.Id == query.PaymentId && p.PaidAt != null
                    select new RefundablePayment(p.Id, p.UserId, p.PaidAt!.Value, p.Amount, p.Provider, t.Name, p.DaysGranted, p.Status))
                .FirstOrDefaultAsync(cancellationToken);
            return payment is null ? PaymentErrors.NotFound(query.PaymentId) : payment;
        }
    }
}
