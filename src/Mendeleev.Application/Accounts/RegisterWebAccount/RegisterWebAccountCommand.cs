using Mendeleev.Application.Abstractions.Accounts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Mendeleev.Application.Accounts.RegisterWebAccount
{
    /// <summary>
    /// Registration on the site without Telegram (FR-ACC-05, FR-WEB-02): a new account and its key,
    /// returned once in display form. The trial is not given here — it is tied to the Telegram ID (ТЗ 27).
    /// </summary>
    public sealed record RegisterWebAccountCommand : ICommand<NewWebAccount>;

    public sealed record NewWebAccount(long UserId, string AccountKey, Guid SessionStamp);

    internal sealed class RegisterWebAccountCommandHandler(
        IApplicationDbContext db,
        IAccountKeyHasher hasher,
        IDateTimeProvider clock)
        : ICommandHandler<RegisterWebAccountCommand, NewWebAccount>
    {
        private const int MaxAttempts = 5;

        public async Task<Result<NewWebAccount>> Handle(RegisterWebAccountCommand command, CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                AccountKey key = AccountKey.Generate();
                string hash = hasher.Hash(key);

                if (await db.Users.AnyAsync(u => u.AccountKeyHash == hash, cancellationToken))
                {
                    continue;
                }

                var user = User.CreateForWeb(hash, clock.UtcNow);
                db.Users.Add(user);
                await db.SaveChangesAsync(cancellationToken);

                return new NewWebAccount(user.Id, key.ToDisplayString(), user.SessionStamp);
            }

            return Result.Failure<NewWebAccount>(Error.Failure("Accounts.KeyGeneration", "Не удалось создать аккаунт, попробуйте ещё раз."));
        }
    }
}
