using FluentValidation;
using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Staff;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Admin.Staff
{
    /// <summary>
    /// Staff management (FR-ADM-10): admins add, re-role and disable support and admins; the tech admin
    /// role is handed out and touched only by a tech admin. Changes take effect with the next command.
    /// </summary>
    public sealed record ListStaffQuery(long StaffId) : IQuery<IReadOnlyList<StaffIdentityView>>;

    public sealed record StaffIdentityView(long Id, long TelegramId, StaffRole Role, string DisplayName, bool IsActive);

    public sealed record AddStaffCommand(long ActorStaffId, long TelegramId, StaffRole Role, string DisplayName) : ICommand<long>;

    public sealed record ChangeStaffRoleCommand(long ActorStaffId, long TargetStaffId, StaffRole Role) : ICommand;

    public sealed record SetStaffActiveCommand(long ActorStaffId, long TargetStaffId, bool IsActive) : ICommand;

    internal sealed class AddStaffCommandValidator : AbstractValidator<AddStaffCommand>
    {
        public AddStaffCommandValidator()
        {
            RuleFor(x => x.TelegramId).GreaterThan(0);
            RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(64);
        }
    }

    internal sealed class StaffManagementHandler(
        IApplicationDbContext db,
        IStaffAuthorizer authorizer,
        IDateTimeProvider clock)
        : IQueryHandler<ListStaffQuery, IReadOnlyList<StaffIdentityView>>,
          ICommandHandler<AddStaffCommand, long>,
          ICommandHandler<ChangeStaffRoleCommand>,
          ICommandHandler<SetStaffActiveCommand>
    {
        public async Task<Result<IReadOnlyList<StaffIdentityView>>> Handle(ListStaffQuery query, CancellationToken cancellationToken)
        {
            Result<StaffMember> actor = await authorizer.AuthorizeAsync(query.StaffId, StaffPermission.ManageStaff, cancellationToken);
            if (actor.IsFailure)
            {
                return actor.Error;
            }

            List<StaffIdentityView> staff = await db.Staff
                .AsNoTracking()
                .OrderBy(s => s.Role).ThenBy(s => s.DisplayName)
                .Select(s => new StaffIdentityView(s.Id, s.TelegramId, s.Role, s.DisplayName, s.IsActive))
                .ToListAsync(cancellationToken);

            return Result.Success<IReadOnlyList<StaffIdentityView>>(staff);
        }

        public async Task<Result<long>> Handle(AddStaffCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> actor = await authorizer.AuthorizeAsync(command.ActorStaffId, StaffPermission.ManageStaff, cancellationToken);
            if (actor.IsFailure)
            {
                return actor.Error;
            }
            if (!StaffPolicy.CanManage(actor.Value.Role, command.Role))
            {
                return StaffErrors.CannotManageRole;
            }
            if (await db.Staff.AnyAsync(s => s.TelegramId == command.TelegramId, cancellationToken))
            {
                return StaffErrors.AlreadyExists;
            }

            DateTime now = clock.UtcNow;
            var member = StaffMember.Create(command.TelegramId, command.Role, command.DisplayName, actor.Value.Id, now);
            db.Staff.Add(member);
            await db.SaveChangesAsync(cancellationToken);

            db.AuditLog.Add(AuditLogEntry.ByStaff(actor.Value.Id, AuditActions.StaffAdd, null,
                Json.Serialize(new { staffId = member.Id, role = command.Role.ToString(), name = member.DisplayName }), now));
            await db.SaveChangesAsync(cancellationToken);
            return member.Id;
        }

        public async Task<Result> Handle(ChangeStaffRoleCommand command, CancellationToken cancellationToken)
        {
            Result<(StaffMember Actor, StaffMember Target)> pair = await LoadAsync(command.ActorStaffId, command.TargetStaffId, cancellationToken);
            if (pair.IsFailure)
            {
                return pair;
            }
            (StaffMember actor, StaffMember target) = pair.Value;

            if (!StaffPolicy.CanManage(actor.Role, target.Role) || !StaffPolicy.CanManage(actor.Role, command.Role))
            {
                return Result.Failure(StaffErrors.CannotManageRole);
            }

            DateTime now = clock.UtcNow;
            StaffRole before = target.Role;
            target.ChangeRole(command.Role, now);
            db.AuditLog.Add(AuditLogEntry.ByStaff(actor.Id, AuditActions.StaffRoleChange, null,
                Json.Serialize(new { staffId = target.Id, before = before.ToString(), after = command.Role.ToString() }), now));
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        public async Task<Result> Handle(SetStaffActiveCommand command, CancellationToken cancellationToken)
        {
            Result<(StaffMember Actor, StaffMember Target)> pair = await LoadAsync(command.ActorStaffId, command.TargetStaffId, cancellationToken);
            if (pair.IsFailure)
            {
                return pair;
            }
            (StaffMember actor, StaffMember target) = pair.Value;

            if (!StaffPolicy.CanManage(actor.Role, target.Role))
            {
                return Result.Failure(StaffErrors.CannotManageRole);
            }

            DateTime now = clock.UtcNow;
            target.SetActive(command.IsActive, now);
            db.AuditLog.Add(AuditLogEntry.ByStaff(actor.Id, command.IsActive ? AuditActions.StaffEnable : AuditActions.StaffDisable, null,
                Json.Serialize(new { staffId = target.Id }), now));
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        private async Task<Result<(StaffMember Actor, StaffMember Target)>> LoadAsync(long actorId, long targetId, CancellationToken cancellationToken)
        {
            Result<StaffMember> actor = await authorizer.AuthorizeAsync(actorId, StaffPermission.ManageStaff, cancellationToken);
            if (actor.IsFailure)
            {
                return actor.Error;
            }
            if (actorId == targetId)
            {
                return StaffErrors.CannotChangeSelf;
            }

            StaffMember? target = await db.Staff.FirstOrDefaultAsync(s => s.Id == targetId, cancellationToken);
            if (target is null)
            {
                return StaffErrors.NotFound;
            }

            return Result.Success((actor.Value, target));
        }
    }
}
