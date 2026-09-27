using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Broadcasts
{
    /// <summary>
    /// A broadcast by an admin (ТЗ 28, «Рассылки»). Delivery walks the recipients in id order and keeps
    /// <see cref="CursorUserId"/>, so a restart resumes where it stopped instead of sending twice.
    /// </summary>
    public sealed class Broadcast
    {
        public const int MaxTextLength = 4000;

        private Broadcast() { }

        public long Id { get; private set; }

        /// <summary>Telegram HTML markup.</summary>
        public string Text { get; private set; } = string.Empty;

        public BroadcastSegment Segment { get; private set; }

        /// <summary>Adds the «Как обновить подписку» button (incident template).</summary>
        public bool WithUpdateButton { get; private set; }

        public BroadcastStatus Status { get; private set; }

        public int Total { get; private set; }

        public int Sent { get; private set; }

        public int Failed { get; private set; }

        public int BotBlocked { get; private set; }

        public long CursorUserId { get; private set; }

        public long CreatedByStaffId { get; private set; }

        /// <summary>Chat of the staff member who gets the progress and the final report.</summary>
        public long ReportChatId { get; private set; }

        public DateTime CreatedAt { get; private set; }

        public DateTime? StartedAt { get; private set; }

        public DateTime? FinishedAt { get; private set; }

        public static Broadcast Draft(string text, BroadcastSegment segment, bool withUpdateButton, int total, long staffId, long reportChatId, DateTime utcNow)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);

            return new Broadcast
            {
                Text = text,
                Segment = segment,
                WithUpdateButton = withUpdateButton,
                Status = BroadcastStatus.Draft,
                Total = total,
                CreatedByStaffId = staffId,
                ReportChatId = reportChatId,
                CreatedAt = utcNow,
            };
        }

        public Result Start(int total, DateTime utcNow)
        {
            if (Status != BroadcastStatus.Draft)
            {
                return Result.Failure(BroadcastErrors.NotDraft);
            }
            Status = BroadcastStatus.Sending;
            Total = total;
            StartedAt = utcNow;
            return Result.Success();
        }

        public Result Cancel(DateTime utcNow)
        {
            if (Status is not (BroadcastStatus.Draft or BroadcastStatus.Sending))
            {
                return Result.Failure(BroadcastErrors.AlreadyFinished);
            }
            Status = BroadcastStatus.Canceled;
            FinishedAt = utcNow;
            return Result.Success();
        }

        public void RecordSent(long userId)
        {
            Sent++;
            CursorUserId = userId;
        }

        public void RecordBotBlocked(long userId)
        {
            Failed++;
            BotBlocked++;
            CursorUserId = userId;
        }

        public void RecordFailed(long userId)
        {
            Failed++;
            CursorUserId = userId;
        }

        public void Complete(DateTime utcNow)
        {
            if (Status != BroadcastStatus.Sending)
            {
                return;
            }
            Status = BroadcastStatus.Done;
            FinishedAt = utcNow;
        }
    }
}
