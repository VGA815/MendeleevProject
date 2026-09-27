using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Devices;
using Mendeleev.Domain.Staff;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Admin.Users
{
    /// <summary>
    /// Link reissue (FR-PNL-16, FR-ADM-11) when a link leaked or is being resold: the old link stops
    /// working, the user gets the new one with a warning that devices must be added again. Done in the
    /// panel right away; if the panel is down, the staff member is told.
    /// </summary>
    public sealed record ReissueLinkCommand(long StaffId, long UserId) : ICommand;

    internal sealed class ReissueLinkCommandHandler(
        IApplicationDbContext db,
        IStaffAuthorizer authorizer,
        IPanelClient panel,
        IDateTimeProvider clock)
        : ICommandHandler<ReissueLinkCommand>
    {
        public async Task<Result> Handle(ReissueLinkCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.ReissueLink, cancellationToken);
            if (staff.IsFailure)
            {
                return staff;
            }

            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);
            await db.LockUserAsync(command.UserId, cancellationToken);

            Subscription? subscription = await db.Subscriptions
                .Include(s => s.Tariff)
                .FirstOrDefaultAsync(s => s.UserId == command.UserId, cancellationToken);
            if (subscription?.PanelUserId is not int panelUserId || subscription.Status == SubscriptionStatus.Archived)
            {
                return Result.Failure(DeviceErrors.NoAccess);
            }

            string newShortUuid = PanelIdentifiers.NewShortUuid();
            PanelUser revoked;
            try
            {
                revoked = await panel.RevokeSubscriptionAsync(panelUserId, newShortUuid, cancellationToken);
            }
            catch (PanelException)
            {
                return Result.Failure(DeviceErrors.PanelUnavailable);
            }

            DateTime now = clock.UtcNow;
            subscription.ApplyReissuedLink(revoked.ShortUuid, revoked.VlessUuid, revoked.SubscriptionUrl, now);
            db.AuditLog.Add(AuditLogEntry.ByStaff(command.StaffId, AuditActions.LinkReissue, command.UserId, Json.Serialize(new { panelUserId }), now));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
    }
}
