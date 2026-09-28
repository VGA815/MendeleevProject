using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Accounts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mendeleev.Application.Accounts.LinkTelegram
{
    /// <summary>
    /// Links Telegram to the signed-in web account by the code from the bot (FR-ACC-09, FR-WEB-08) and
    /// merges the two accounts (FR-ACC-10, ТЗ 21, решение 24.09):
    /// 1. the Telegram account has no subscription and no payments → its Telegram ID and trial mark move
    ///    into the web account, the Telegram account is deleted;
    /// 2. otherwise, the web account has none → its key and email move into the Telegram account, the web
    ///    account is deleted;
    /// 3. otherwise the link is refused and the user is sent to support (manual merge is stage 2).
    /// </summary>
    /// <remarks>
    /// The result names the account that remains: after rule 2 the site must sign the user in again as it.
    /// </remarks>
    public sealed record LinkTelegramCommand(long WebUserId, string Code) : ICommand<LinkedAccount>;

    public sealed record LinkedAccount(long UserId, Guid SessionStamp, AccountMergeRule Rule);

    public enum AccountMergeRule
    {
        TelegramIntoWeb = 1,
        WebIntoTelegram = 2,
    }

    internal sealed class LinkTelegramCommandHandler(
        IApplicationDbContext db,
        IAccountKeyHasher hasher,
        IDateTimeProvider clock)
        : ICommandHandler<LinkTelegramCommand, LinkedAccount>
    {
        public async Task<Result<LinkedAccount>> Handle(LinkTelegramCommand command, CancellationToken cancellationToken)
        {
            if (!LinkCode.TryNormalizeTelegramCode(command.Code, out string code))
            {
                return UserErrors.InvalidLinkCode;
            }

            string hash = hasher.HashLinkCode(code);
            DateTime now = clock.UtcNow;

            long? telegramUserId = await db.LinkCodes
                .AsNoTracking()
                .Where(c => c.CodeHash == hash && c.Purpose == LinkCodePurpose.LinkTelegram && c.UsedAt == null && c.ExpiresAt > now)
                .Select(c => (long?)c.UserId)
                .FirstOrDefaultAsync(cancellationToken);
            if (telegramUserId is not long telegramId)
            {
                return UserErrors.InvalidLinkCode;
            }
            if (telegramId == command.WebUserId)
            {
                return UserErrors.TelegramAlreadyLinked;
            }

            await using IDbContextTransaction transaction = await db.BeginTransactionAsync(cancellationToken);

            // Both accounts may change their subscription owner, so both rows are locked — in id order,
            // so that two links racing for the same pair cannot deadlock.
            var locked = new Dictionary<long, User>();
            foreach (long userId in new[] { telegramId, command.WebUserId }.Order())
            {
                if (await db.LockUserAsync(userId, cancellationToken) is User user)
                {
                    locked[userId] = user;
                }
            }

            User? web = locked.GetValueOrDefault(command.WebUserId);
            User? telegram = locked.GetValueOrDefault(telegramId);
            if (web is null)
            {
                return UserErrors.NotFound(command.WebUserId);
            }

            // Re-read under the locks: a parallel link may have used the code or deleted its owner.
            LinkCode? linkCode = await db.LinkCodes.FirstOrDefaultAsync(
                c => c.CodeHash == hash && c.UserId == telegramId && c.Purpose == LinkCodePurpose.LinkTelegram,
                cancellationToken);
            if (telegram?.TelegramId is null || linkCode is null || !linkCode.IsUsable(now))
            {
                return UserErrors.InvalidLinkCode;
            }
            if (web.TelegramId is not null)
            {
                return UserErrors.TelegramAlreadyLinked;
            }
            if (web.IsBlocked || telegram.IsBlocked)
            {
                return UserErrors.Blocked;
            }

            AccountMergeRule rule;
            User survivor;
            if (!await HasSubscriptionOrPaymentsAsync(telegram.Id, cancellationToken))
            {
                rule = AccountMergeRule.TelegramIntoWeb;
                survivor = web;

                // Delete first and save: the Telegram ID is unique. The code goes with its owner (cascade).
                db.Users.Remove(telegram);
                await db.SaveChangesAsync(cancellationToken);

                web.TakeOverTelegram(telegram, now);
            }
            else if (!await HasSubscriptionOrPaymentsAsync(web.Id, cancellationToken))
            {
                rule = AccountMergeRule.WebIntoTelegram;
                survivor = telegram;

                // Delete first and save: the key hash and the email are unique.
                db.Users.Remove(web);
                await db.SaveChangesAsync(cancellationToken);

                telegram.TakeOverWebCredentials(web, now);
                linkCode.MarkUsed(now);
            }
            else
            {
                return UserErrors.MergeNeedsSupport;
            }

            db.AuditLog.Add(AuditLogEntry.BySystem(
                AuditActions.AccountsMerged,
                survivor.Id,
                Json.Serialize(new { rule = rule.ToString(), web_user_id = web.Id, telegram_user_id = telegram.Id, kept_user_id = survivor.Id }),
                now));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new LinkedAccount(survivor.Id, survivor.SessionStamp, rule);
        }

        private async Task<bool> HasSubscriptionOrPaymentsAsync(long userId, CancellationToken cancellationToken) =>
            await db.Subscriptions.AnyAsync(s => s.UserId == userId, cancellationToken)
            || await db.Payments.AnyAsync(p => p.UserId == userId, cancellationToken);
    }
}
