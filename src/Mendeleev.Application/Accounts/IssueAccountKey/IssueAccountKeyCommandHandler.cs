using Mendeleev.Application.Abstractions.Accounts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Accounts.IssueAccountKey
{
    internal sealed class IssueAccountKeyCommandHandler(
        IApplicationDbContext db,
        IAccountKeyHasher hasher,
        IDateTimeProvider clock)
        : ICommandHandler<IssueAccountKeyCommand, string>
    {
        private const int MaxAttempts = 5;

        public async Task<Result<string>> Handle(IssueAccountKeyCommand command, CancellationToken cancellationToken)
        {
            User? user = await db.Users.FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);
            if (user is null)
            {
                return UserErrors.NotFound(command.UserId);
            }
            if (user.IsBlocked)
            {
                return UserErrors.Blocked;
            }

            // A collision of 16 random digits is practically impossible, but the unique index would
            // reject it, so draw again rather than fail.
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                AccountKey key = AccountKey.Generate();
                string hash = hasher.Hash(key);

                if (await db.Users.AnyAsync(u => u.AccountKeyHash == hash, cancellationToken))
                {
                    continue;
                }

                user.SetAccountKey(hash, clock.UtcNow);
                await db.SaveChangesAsync(cancellationToken);
                return key.ToDisplayString();
            }

            return Result.Failure<string>(Error.Failure("Accounts.KeyGeneration", "Не удалось выпустить ключ, попробуйте ещё раз."));
        }
    }
}
