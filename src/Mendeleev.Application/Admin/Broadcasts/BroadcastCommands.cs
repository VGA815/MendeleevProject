using System.Security.Cryptography;
using System.Text;
using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Broadcasts;
using Mendeleev.Domain.Staff;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Admin.Broadcasts
{
    /// <summary>Recipients per segment, shown before the text is asked for (ТЗ 28, «Рассылки»).</summary>
    public sealed record CountRecipientsQuery(long StaffId) : IQuery<IReadOnlyDictionary<BroadcastSegment, int>>;

    /// <summary>A draft: the text is previewed to the author before anything is sent.</summary>
    public sealed record CreateBroadcastCommand(long StaffId, long ReportChatId, BroadcastSegment Segment, string Text, bool WithUpdateButton)
        : ICommand<BroadcastDraft>;

    public sealed record BroadcastDraft(long BroadcastId, int Recipients);

    /// <summary>«Отправить N получателям» — needs this explicit confirmation.</summary>
    public sealed record StartBroadcastCommand(long StaffId, long BroadcastId) : ICommand<int>;

    public sealed record CancelBroadcastCommand(long StaffId, long BroadcastId) : ICommand;

    internal sealed class BroadcastCommandsHandler(
        IApplicationDbContext db,
        IStaffAuthorizer authorizer,
        IDateTimeProvider clock)
        : IQueryHandler<CountRecipientsQuery, IReadOnlyDictionary<BroadcastSegment, int>>,
          ICommandHandler<CreateBroadcastCommand, BroadcastDraft>,
          ICommandHandler<StartBroadcastCommand, int>,
          ICommandHandler<CancelBroadcastCommand>
    {
        public async Task<Result<IReadOnlyDictionary<BroadcastSegment, int>>> Handle(CountRecipientsQuery query, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(query.StaffId, StaffPermission.Broadcast, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            var counts = new Dictionary<BroadcastSegment, int>();
            foreach (BroadcastSegment segment in Enum.GetValues<BroadcastSegment>())
            {
                counts[segment] = await BroadcastRecipients.Query(db, segment).CountAsync(cancellationToken);
            }

            return Result.Success<IReadOnlyDictionary<BroadcastSegment, int>>(counts);
        }

        public async Task<Result<BroadcastDraft>> Handle(CreateBroadcastCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.Broadcast, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }
            if (string.IsNullOrWhiteSpace(command.Text) || command.Text.Length > Broadcast.MaxTextLength)
            {
                return BroadcastErrors.TextTooLong;
            }

            int total = await BroadcastRecipients.Query(db, command.Segment).CountAsync(cancellationToken);
            if (total == 0)
            {
                return BroadcastErrors.NoRecipients;
            }

            var broadcast = Broadcast.Draft(command.Text, command.Segment, command.WithUpdateButton, total, command.StaffId, command.ReportChatId, clock.UtcNow);
            db.Broadcasts.Add(broadcast);
            await db.SaveChangesAsync(cancellationToken);

            return new BroadcastDraft(broadcast.Id, total);
        }

        public async Task<Result<int>> Handle(StartBroadcastCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.Broadcast, cancellationToken);
            if (staff.IsFailure)
            {
                return staff.Error;
            }

            Broadcast? broadcast = await db.Broadcasts.FirstOrDefaultAsync(b => b.Id == command.BroadcastId, cancellationToken);
            if (broadcast is null)
            {
                return BroadcastErrors.NotFound;
            }

            int total = await BroadcastRecipients.Query(db, broadcast.Segment).CountAsync(cancellationToken);
            DateTime now = clock.UtcNow;
            Result started = broadcast.Start(total, now);
            if (started.IsFailure)
            {
                return started.Error;
            }

            // The audit keeps the segment, the audience and a hash of the text, not the text itself.
            string textHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(broadcast.Text)));
            db.AuditLog.Add(AuditLogEntry.ByStaff(
                command.StaffId,
                AuditActions.BroadcastSend,
                null,
                Json.Serialize(new { broadcastId = broadcast.Id, segment = broadcast.Segment.ToString(), recipients = total, textHash }),
                now));

            await db.SaveChangesAsync(cancellationToken);
            return total;
        }

        public async Task<Result> Handle(CancelBroadcastCommand command, CancellationToken cancellationToken)
        {
            Result<StaffMember> staff = await authorizer.AuthorizeAsync(command.StaffId, StaffPermission.Broadcast, cancellationToken);
            if (staff.IsFailure)
            {
                return staff;
            }

            Broadcast? broadcast = await db.Broadcasts.FirstOrDefaultAsync(b => b.Id == command.BroadcastId, cancellationToken);
            if (broadcast is null)
            {
                return Result.Failure(BroadcastErrors.NotFound);
            }

            DateTime now = clock.UtcNow;
            Result canceled = broadcast.Cancel(now);
            if (canceled.IsFailure)
            {
                return canceled;
            }

            db.AuditLog.Add(AuditLogEntry.ByStaff(command.StaffId, AuditActions.BroadcastCancel, null, Json.Serialize(new { broadcastId = broadcast.Id, sent = broadcast.Sent }), now));
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
    }
}
