using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Staff;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Admin.Staff
{
    /// <summary>Is the sender an active staff member? Asked for every message, so disabling works at once.</summary>
    public sealed record GetActiveStaffQuery(long TelegramId) : IQuery<StaffIdentity?>;

    public sealed record StaffIdentity(long StaffId, long TelegramId, StaffRole Role, string DisplayName)
    {
        public bool Can(StaffPermission permission) => StaffPolicy.Allows(Role, permission);
    }

    internal sealed class GetActiveStaffQueryHandler(IApplicationDbContext db) : IQueryHandler<GetActiveStaffQuery, StaffIdentity?>
    {
        public async Task<Result<StaffIdentity?>> Handle(GetActiveStaffQuery query, CancellationToken cancellationToken)
        {
            StaffIdentity? identity = await db.Staff
                .AsNoTracking()
                .Where(s => s.TelegramId == query.TelegramId && s.IsActive)
                .Select(s => new StaffIdentity(s.Id, s.TelegramId, s.Role, s.DisplayName))
                .FirstOrDefaultAsync(cancellationToken);

            return Result.Success(identity);
        }
    }
}
