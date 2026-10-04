using FluentValidation;
using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Admin.Users
{
    /// <summary>
    /// Block (admin and tech admin, with a reason): the subscription becomes <c>disabled</c> and access is
    /// switched off in the panel within a minute (FR-ACC-03, FR-SUB-13, FR-ADM-06).
    /// </summary>
    public sealed record BlockUserCommand(long StaffId, long UserId, string Reason) : ICommand;

    /// <summary>Unblock: back to active or expired depending on the term.</summary>
    public sealed record UnblockUserCommand(long StaffId, long UserId) : ICommand;

    internal sealed class BlockUserCommandValidator : AbstractValidator<BlockUserCommand>
    {
        public BlockUserCommandValidator()
        {
            // One rule with one message: WithMessage only covers the validator right before it.
            RuleFor(x => x.Reason).Must(r => r?.Trim().Length is >= 3 and <= 500).WithMessage(StaffErrors.ReasonRequired.Description);
        }
    }

    internal sealed class BlockUserCommandHandler(
        IApplicationDbContext db,
        IStaffAuthorizer authorizer,
        IDateTimeProvider clock)
        : ICommandHandler<BlockUserCommand>, ICommandHandler<UnblockUserCommand>
    {
        public Task<Result> Handle(BlockUserCommand command, CancellationToken cancellationToken) =>
            ChangeAsync(command.StaffId, command.UserId, block: true, command.Reason.Trim(), cancellationToken);

        public Task<Result> Handle(UnblockUserCommand command, CancellationToken cancellationToken) =>
            ChangeAsync(command.StaffId, command.UserId, block: false, reason: null, cancellationToken);

        private async Task<Result> ChangeAsync(long staffId, long userId, bool block, string? reason, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(staffId, StaffPermission.BlockUsers, cancellationToken);
            if (staff.IsFailure)
            {
                return staff;
            }

            DateTime now = clock.UtcNow;
            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);

            User? user = await db.LockUserAsync(userId, cancellationToken);
            if (user is null)
            {
                return Result.Failure(UserErrors.NotFound(userId));
            }

            Result changed = block ? user.Block(now) : user.Unblock(now);
            if (changed.IsFailure)
            {
                return changed;
            }

            Subscription? subscription = await db.Subscriptions
                .Include(s => s.Tariff)
                .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);

            string? statusBefore = subscription?.Status.ToString();
            if (block)
            {
                subscription?.Disable(now);
            }
            else
            {
                subscription?.Enable(now);
            }

            db.AuditLog.Add(AuditLogEntry.ByStaff(
                staffId,
                block ? AuditActions.UserBlock : AuditActions.UserUnblock,
                userId,
                Json.Serialize(new { reason, subscriptionBefore = statusBefore, subscriptionAfter = subscription?.Status.ToString() }),
                now));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
    }
}
