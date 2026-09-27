using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Staff;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Admin.Audit
{
    /// <summary><c>/audit</c>: the latest actions on a user or by a staff member (FR-ADM-07).</summary>
    public sealed record GetAuditQuery(long StaffId, long? TargetUserId, long? ActorStaffId, int Limit = 20)
        : IQuery<IReadOnlyList<AuditEntryView>>;

    public sealed record AuditEntryView(
        DateTime CreatedAt,
        AuditActorType ActorType,
        string? ActorName,
        string Action,
        long? TargetUserId,
        string? Details);

    internal sealed class GetAuditQueryHandler(IApplicationDbContext db, IStaffAuthorizer authorizer)
        : IQueryHandler<GetAuditQuery, IReadOnlyList<AuditEntryView>>
    {
        public async Task<Result<IReadOnlyList<AuditEntryView>>> Handle(GetAuditQuery query, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(query.StaffId, StaffPermission.ViewAudit, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            IQueryable<AuditLogEntry> entries = db.AuditLog.AsNoTracking();
            if (query.TargetUserId is long userId)
            {
                entries = entries.Where(a => a.TargetUserId == userId);
            }
            if (query.ActorStaffId is long actorId)
            {
                entries = entries.Where(a => a.StaffId == actorId);
            }

            List<AuditEntryView> result = await entries
                .OrderByDescending(a => a.Id)
                .Take(Math.Clamp(query.Limit, 1, 100))
                .Select(a => new AuditEntryView(
                    a.CreatedAt,
                    a.ActorType,
                    db.Staff.Where(s => s.Id == a.StaffId).Select(s => s.DisplayName).FirstOrDefault(),
                    a.Action,
                    a.TargetUserId,
                    a.Details))
                .ToListAsync(cancellationToken);

            return Result.Success<IReadOnlyList<AuditEntryView>>(result);
        }
    }
}
