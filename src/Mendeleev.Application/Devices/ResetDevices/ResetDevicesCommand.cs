using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Domain.Common;
using Mendeleev.Domain.Devices;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Devices.ResetDevices
{
    /// <summary>
    /// «Сбросить все устройства» by the user: not more than twice in 30 days (FR-PNL-10). Devices register
    /// again at the next subscription update in the client. The panel is called directly, not through the
    /// outbox: if it is down, the user is told so instead of a silently queued reset. The limit is checked
    /// under the user's row lock: a double click or the bot and the cabinet at once cannot make a third reset.
    /// </summary>
    public sealed record ResetDevicesCommand(long UserId) : ICommand<DevicesResetResult>;

    public sealed record DevicesResetResult(int Removed, DateTime? NextResetAvailableAtUtc);

    internal sealed class ResetDevicesCommandHandler(
        IApplicationDbContext db,
        IPanelClient panel,
        IDateTimeProvider clock)
        : ICommandHandler<ResetDevicesCommand, DevicesResetResult>
    {
        public async Task<Result<DevicesResetResult>> Handle(ResetDevicesCommand command, CancellationToken cancellationToken)
        {
            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);

            User? user = await db.LockUserAsync(command.UserId, cancellationToken);
            if (user is null)
            {
                return UserErrors.NotFound(command.UserId);
            }
            if (user.IsBlocked)
            {
                return UserErrors.Blocked;
            }

            Subscription? subscription = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == user.Id, cancellationToken);
            if (subscription?.PanelUserId is not int panelUserId)
            {
                return DeviceErrors.NoAccess;
            }

            DateTime now = clock.UtcNow;
            DateTime windowStart = now - DeviceReset.Window;
            List<DateTime> resets = await db.DeviceResets
                .AsNoTracking()
                .Where(r => r.UserId == user.Id && r.InitiatedBy == DeviceResetInitiator.User && r.CreatedAt > windowStart)
                .Select(r => r.CreatedAt)
                .ToListAsync(cancellationToken);

            if (DeviceReset.NextUserResetAvailableAt(resets, now) is DateTime availableAt)
            {
                return DeviceErrors.ResetLimitReached(MoscowTime.FromUtc(availableAt));
            }

            int removed;
            try
            {
                removed = (await panel.GetDevicesAsync(panelUserId, cancellationToken)).Count;
                await panel.DeleteAllDevicesAsync(panelUserId, cancellationToken);
            }
            catch (PanelException)
            {
                return DeviceErrors.PanelUnavailable;
            }

            db.DeviceResets.Add(DeviceReset.ByUser(user.Id, removed, now));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            resets.Add(now);
            return new DevicesResetResult(removed, DeviceReset.NextUserResetAvailableAt(resets, now));
        }
    }
}
