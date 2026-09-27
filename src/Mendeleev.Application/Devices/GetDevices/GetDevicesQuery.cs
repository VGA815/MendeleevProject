using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Domain.Common;
using Mendeleev.Domain.Devices;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Devices.GetDevices
{
    /// <summary>«Устройства» (FR-PNL-10, FR-BOT-09): the list from the panel plus when the next reset is possible.</summary>
    public sealed record GetDevicesQuery(long UserId) : IQuery<DevicesView>;

    public sealed record DevicesView(
        IReadOnlyList<PanelDevice> Devices,
        int DeviceLimit,
        DateTime? NextResetAvailableAtUtc);

    internal sealed class GetDevicesQueryHandler(
        IApplicationDbContext db,
        IPanelClient panel,
        IDateTimeProvider clock)
        : IQueryHandler<GetDevicesQuery, DevicesView>
    {
        public async Task<Result<DevicesView>> Handle(GetDevicesQuery query, CancellationToken cancellationToken)
        {
            Subscription? subscription = await db.Subscriptions
                .AsNoTracking()
                .Include(s => s.Tariff)
                .FirstOrDefaultAsync(s => s.UserId == query.UserId, cancellationToken);

            if (subscription?.PanelUserId is not int panelUserId)
            {
                return DeviceErrors.NoAccess;
            }

            IReadOnlyList<PanelDevice> devices;
            try
            {
                devices = await panel.GetDevicesAsync(panelUserId, cancellationToken);
            }
            catch (PanelException)
            {
                return DeviceErrors.PanelUnavailable;
            }

            DateTime now = clock.UtcNow;
            DateTime windowStart = now - DeviceReset.Window;
            List<DateTime> resets = await db.DeviceResets
                .AsNoTracking()
                .Where(r => r.UserId == query.UserId && r.InitiatedBy == DeviceResetInitiator.User && r.CreatedAt > windowStart)
                .Select(r => r.CreatedAt)
                .ToListAsync(cancellationToken);

            return new DevicesView(
                devices.OrderBy(d => d.CreatedAt).ToList(),
                subscription.Tariff.DeviceLimit,
                DeviceReset.NextUserResetAvailableAt(resets, now));
        }
    }
}
