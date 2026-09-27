using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Domain.Staff;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Admin
{
    /// <summary>
    /// Re-reads the staff member on every action: a disabled member or a changed role takes effect with
    /// the next command (FR-ADM-10), and the check does not rely on what the bot chose to show.
    /// </summary>
    public interface IStaffAuthorizer
    {
        Task<Result<StaffMember>> AuthorizeAsync(long staffId, StaffPermission permission, CancellationToken cancellationToken);
    }

    internal sealed class StaffAuthorizer(IApplicationDbContext db) : IStaffAuthorizer
    {
        public async Task<Result<StaffMember>> AuthorizeAsync(long staffId, StaffPermission permission, CancellationToken cancellationToken)
        {
            StaffMember? staff = await db.Staff.AsNoTracking().FirstOrDefaultAsync(s => s.Id == staffId, cancellationToken);

            if (staff is null || !staff.IsActive || !StaffPolicy.Allows(staff.Role, permission))
            {
                return StaffErrors.NotAllowed;
            }

            return staff;
        }
    }
}
