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

namespace Mendeleev.Application.Admin.Users
{
    /// <summary>Staff resets all devices of a user — no 30-day limit, but audited (FR-ADM-05).</summary>
    public sealed record StaffResetDevicesCommand(long StaffId, long UserId) : ICommand<int>;

    /// <summary>Staff removes one device (FR-PNL-10: only staff can remove devices one by one).</summary>
    public sealed record StaffDeleteDeviceCommand(long StaffId, long UserId, string Hwid) : ICommand;

    internal sealed class StaffDeviceCommandsHandler(
        IApplicationDbContext db,
        IStaffAuthorizer authorizer,
        IPanelClient panel,
        IDateTimeProvider clock)
        : ICommandHandler<StaffResetDevicesCommand, int>, ICommandHandler<StaffDeleteDeviceCommand>
    {
        public async Task<Result<int>> Handle(StaffResetDevicesCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.ManageDevices, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            if (await PanelUserIdAsync(command.UserId, cancellationToken) is not int panelUserId)
            {
                return DeviceErrors.NoAccess;
            }

            int removed;
            try
            {
                removed = (await panel.GetDevicesAsync(panelUserId, cancellationToken)).Count;
                await panel.DeleteAllDevicesAsync(panelUserId, cancellationToken);
            }
            catch (PanelException)
            {
                // Not queued silently: the staff member is told and retries (ТЗ 28, «Обработка ошибок»).
                return DeviceErrors.PanelUnavailable;
            }

            DateTime now = clock.UtcNow;
            db.DeviceResets.Add(DeviceReset.ByStaff(command.UserId, command.StaffId, removed, now));
            db.AuditLog.Add(AuditLogEntry.ByStaff(command.StaffId, AuditActions.DevicesReset, command.UserId, Json.Serialize(new { removed }), now));
            await db.SaveChangesAsync(cancellationToken);
            return removed;
        }

        public async Task<Result> Handle(StaffDeleteDeviceCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.ManageDevices, cancellationToken);
            if (staff.IsFailure)
            {
                return staff;
            }

            if (await PanelUserIdAsync(command.UserId, cancellationToken) is not int panelUserId)
            {
                return Result.Failure(DeviceErrors.NoAccess);
            }

            PanelDevice? device;
            try
            {
                IReadOnlyList<PanelDevice> devices = await panel.GetDevicesAsync(panelUserId, cancellationToken);
                device = devices.FirstOrDefault(d => d.Hwid == command.Hwid);
                if (device is null)
                {
                    return Result.Failure(DeviceErrors.DeviceNotFound);
                }
                await panel.DeleteDeviceAsync(panelUserId, command.Hwid, cancellationToken);
            }
            catch (PanelException)
            {
                return Result.Failure(DeviceErrors.PanelUnavailable);
            }

            db.AuditLog.Add(AuditLogEntry.ByStaff(
                command.StaffId,
                AuditActions.DeviceDelete,
                command.UserId,
                Json.Serialize(new { platform = device.Platform, model = device.DeviceModel, added = device.CreatedAt }),
                clock.UtcNow));
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        private Task<int?> PanelUserIdAsync(long userId, CancellationToken cancellationToken) =>
            db.Subscriptions
                .Where(s => s.UserId == userId && s.Status != SubscriptionStatus.Archived)
                .Select(s => s.PanelUserId)
                .FirstOrDefaultAsync(cancellationToken);
    }
}
