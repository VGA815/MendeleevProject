using System.Diagnostics;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Delivery;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Domain.Broadcasts;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mendeleev.Application.Admin.Broadcasts
{
    /// <summary>
    /// Sends a running broadcast within Telegram's limits (FR-BOT-14, NFR-18): not faster than 25 messages
    /// a second overall, waits out <c>retry_after</c> on 429, marks users who blocked the bot. The cursor is
    /// saved as it goes, so a restart resumes instead of repeating; a cancel takes effect within a batch.
    /// </summary>
    public interface IBroadcastDeliveryService
    {
        /// <returns>True if there was a broadcast to work on.</returns>
        Task<bool> RunNextAsync(CancellationToken cancellationToken);
    }

    internal sealed class BroadcastDeliveryService(
        IApplicationDbContext db,
        IUserMessenger messenger,
        IDateTimeProvider clock,
        ILogger<BroadcastDeliveryService> logger)
        : IBroadcastDeliveryService
    {
        private const int BatchSize = 50;
        private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1000.0 / 25);
        private const int MaxRetriesPerRecipient = 5;

        public async Task<bool> RunNextAsync(CancellationToken cancellationToken)
        {
            Broadcast? broadcast = await db.Broadcasts
                .Where(b => b.Status == BroadcastStatus.Sending)
                .OrderBy(b => b.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (broadcast is null)
            {
                return false;
            }

            var pace = Stopwatch.StartNew();
            while (!cancellationToken.IsCancellationRequested)
            {
                // Picks up a cancel made from another scope.
                BroadcastStatus status = await db.Broadcasts.Where(b => b.Id == broadcast.Id).Select(b => b.Status).FirstAsync(cancellationToken);
                if (status != BroadcastStatus.Sending)
                {
                    await ReportAsync(broadcast, status, cancellationToken);
                    return true;
                }

                long cursor = broadcast.CursorUserId;
                var batch = await BroadcastRecipients.Query(db, broadcast.Segment)
                    .AsNoTracking()
                    .Where(u => u.Id > cursor)
                    .OrderBy(u => u.Id)
                    .Select(u => new { u.Id, TelegramId = u.TelegramId!.Value })
                    .Take(BatchSize)
                    .ToListAsync(cancellationToken);

                if (batch.Count == 0)
                {
                    broadcast.Complete(clock.UtcNow);
                    await db.SaveChangesAsync(cancellationToken);
                    await ReportAsync(broadcast, BroadcastStatus.Done, cancellationToken);
                    return true;
                }

                foreach (var recipient in batch)
                {
                    DeliveryResult? result = await SendWithRetriesAsync(recipient.TelegramId, broadcast, pace, cancellationToken);

                    switch (result)
                    {
                        case DeliveryResult.Sent:
                            broadcast.RecordSent(recipient.Id);
                            AppMetrics.BroadcastMessages.Add(1, new KeyValuePair<string, object?>("result", "sent"));
                            break;
                        case DeliveryResult.BotBlocked:
                            broadcast.RecordBotBlocked(recipient.Id);
                            await db.Users.Where(u => u.Id == recipient.Id)
                                .ExecuteUpdateAsync(s => s.SetProperty(u => u.BotBlocked, true), cancellationToken);
                            AppMetrics.BroadcastMessages.Add(1, new KeyValuePair<string, object?>("result", "bot_blocked"));
                            break;
                        default:
                            broadcast.RecordFailed(recipient.Id);
                            AppMetrics.BroadcastMessages.Add(1, new KeyValuePair<string, object?>("result", "failed"));
                            break;
                    }
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            return true;
        }

        private async Task<DeliveryResult?> SendWithRetriesAsync(long chatId, Broadcast broadcast, Stopwatch pace, CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < MaxRetriesPerRecipient; attempt++)
            {
                // Task.Delay drops the fraction of a millisecond and its timer may fire up to a tick early, so
                // 25 short waits in a row add up to more than 25 messages a second: wait until the stopwatch agrees.
                for (TimeSpan wait = MinInterval - pace.Elapsed; wait > TimeSpan.Zero; wait = MinInterval - pace.Elapsed)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(wait.TotalMilliseconds)), cancellationToken);
                }
                pace.Restart();

                try
                {
                    return await messenger.SendBroadcastAsync(chatId, broadcast.Text, broadcast.WithUpdateButton, cancellationToken);
                }
                catch (DeliveryDeferredException ex)
                {
                    TimeSpan retryAfter = ex.RetryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    logger.LogWarning("Broadcast {BroadcastId} paused for {RetryAfter}: {Reason}", broadcast.Id, retryAfter, ex.Message);
                    await Task.Delay(retryAfter, cancellationToken);
                }
            }

            return null;
        }

        private async Task ReportAsync(Broadcast broadcast, BroadcastStatus status, CancellationToken cancellationToken)
        {
            string title = status == BroadcastStatus.Canceled ? "Рассылка остановлена" : "Рассылка завершена";
            string text = $"<b>{title}</b> #{broadcast.Id}\n"
                + $"Получателей: {broadcast.Total}\n"
                + $"Отправлено: {broadcast.Sent}\n"
                + $"Ошибки: {broadcast.Failed}, из них заблокировали бота: {broadcast.BotBlocked}";
            try
            {
                await messenger.SendTextAsync(broadcast.ReportChatId, text, cancellationToken);
            }
            catch (DeliveryDeferredException ex)
            {
                logger.LogWarning(ex, "Could not deliver the report of broadcast {BroadcastId}", broadcast.Id);
            }
        }
    }
}
