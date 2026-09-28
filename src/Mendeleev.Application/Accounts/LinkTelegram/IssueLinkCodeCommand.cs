using Mendeleev.Application.Abstractions.Accounts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Accounts.LinkTelegram
{
    /// <summary>
    /// A code for linking this Telegram account to a web account (FR-ACC-09): 8 digits, 10 minutes, one
    /// use. A new code replaces the previous unused one.
    /// </summary>
    public sealed record IssueLinkCodeCommand(long UserId) : ICommand<IssuedLinkCode>;

    public sealed record IssuedLinkCode(string Code, DateTime ExpiresAtUtc);

    internal sealed class IssueLinkCodeCommandHandler(
        IApplicationDbContext db,
        IAccountKeyHasher hasher,
        IDateTimeProvider clock)
        : ICommandHandler<IssueLinkCodeCommand, IssuedLinkCode>
    {
        private const int MaxAttempts = 5;

        public async Task<Result<IssuedLinkCode>> Handle(IssueLinkCodeCommand command, CancellationToken cancellationToken)
        {
            User? user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);
            if (user is null)
            {
                return UserErrors.NotFound(command.UserId);
            }
            if (user.IsBlocked)
            {
                return UserErrors.Blocked;
            }
            if (user.TelegramId is null)
            {
                return UserErrors.NoTelegram;
            }

            DateTime now = clock.UtcNow;

            await db.LinkCodes
                .Where(c => c.UserId == user.Id && c.Purpose == LinkCodePurpose.LinkTelegram && c.UsedAt == null)
                .ExecuteDeleteAsync(cancellationToken);

            // Two live codes with the same digits would be ambiguous; with 10⁸ codes that is a rare redraw.
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                string code = LinkCode.GenerateTelegramCode();
                string hash = hasher.HashLinkCode(code);

                if (await db.LinkCodes.AnyAsync(c => c.CodeHash == hash && c.UsedAt == null && c.ExpiresAt > now, cancellationToken))
                {
                    continue;
                }

                var linkCode = LinkCode.ForTelegram(user.Id, hash, now);
                db.LinkCodes.Add(linkCode);
                await db.SaveChangesAsync(cancellationToken);

                return new IssuedLinkCode(code, linkCode.ExpiresAt);
            }

            return Result.Failure<IssuedLinkCode>(Error.Failure("Accounts.LinkCodeGeneration", "Не удалось выдать код, попробуйте ещё раз."));
        }
    }
}
