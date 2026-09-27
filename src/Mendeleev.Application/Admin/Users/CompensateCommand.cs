using FluentValidation;
using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Configuration;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Mendeleev.Application.Admin.Users
{
    /// <summary>
    /// Extension as compensation (FR-SUB-12, FR-ADM-04): support within 7 days per action and 14 days per
    /// user in 30 days, admins without a limit. The reason is mandatory; the term and the audit record are
    /// written in one transaction.
    /// </summary>
    public sealed record CompensateCommand(long StaffId, long UserId, int Days, string Reason) : ICommand<DateTime>;

    internal sealed class CompensateCommandValidator : AbstractValidator<CompensateCommand>
    {
        public CompensateCommandValidator()
        {
            RuleFor(x => x.Days).InclusiveBetween(1, 365).WithMessage("Число дней — от 1 до 365.");
            RuleFor(x => x.Reason).NotEmpty().MinimumLength(3).MaximumLength(500).WithMessage(StaffErrors.ReasonRequired.Description);
        }
    }

    internal sealed class CompensateCommandHandler(
        IApplicationDbContext db,
        IStaffAuthorizer authorizer,
        IDateTimeProvider clock,
        IOptions<SubscriptionOptions> options)
        : ICommandHandler<CompensateCommand, DateTime>
    {
        public async Task<Result<DateTime>> Handle(CompensateCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.Compensate, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            DateTime now = clock.UtcNow;
            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);

            User? user = await db.LockUserAsync(command.UserId, cancellationToken);
            if (user is null)
            {
                return UserErrors.NotFound(command.UserId);
            }

            if (StaffPolicy.HasCompensationLimit(staff.Value.Role))
            {
                SubscriptionOptions limits = options.Value;
                int given = await db.SumSupportCompensationDaysAsync(user.Id, now.AddDays(-30), cancellationToken);
                int available = Math.Min(limits.SupportCompensationPerActionDays, limits.SupportCompensationPer30Days - given);
                if (command.Days > available)
                {
                    return SubscriptionErrors.CompensationLimitExceeded(Math.Max(0, available));
                }
            }

            Subscription? subscription = await db.Subscriptions
                .Include(s => s.Tariff)
                .FirstOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);
            if (subscription is null)
            {
                return SubscriptionErrors.NotFound;
            }

            DateTime before = subscription.ExpiresAt;
            Result extended = subscription.ExtendByStaff(command.Days, $"staff:{now.Ticks}", now);
            if (extended.IsFailure)
            {
                return extended.Error;
            }

            db.AuditLog.Add(AuditLogEntry.ByStaff(
                staff.Value.Id,
                AuditActions.SubscriptionExtend,
                user.Id,
                Json.Serialize(new
                {
                    days = command.Days,
                    reason = command.Reason.Trim(),
                    role = staff.Value.Role.ToString(),
                    before,
                    after = subscription.ExpiresAt,
                }),
                now));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return subscription.ExpiresAt;
        }
    }
}
