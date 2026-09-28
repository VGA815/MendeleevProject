using System.Globalization;
using Mendeleev.SharedKernel;

namespace Mendeleev.Domain.Users
{
    /// <summary>
    /// A customer (ТЗ 21, «Аккаунты и вход»). Only the Telegram ID is kept from Telegram — no name,
    /// username, phone or language (FR-ACC-02). The panel sees the user only as <see cref="PanelUsername"/>.
    /// </summary>
    public sealed class User : Entity
    {
        private User() { }

        public long Id { get; private set; }

        public long? TelegramId { get; private set; }

        public string? Email { get; private set; }

        /// <summary>HMAC-SHA256 of the normalized account key; the key itself is never stored.</summary>
        public string? AccountKeyHash { get; private set; }

        public DateTime? AccountKeyIssuedAt { get; private set; }

        /// <summary>
        /// Part of every cabinet session cookie. Changing it ends all sessions: reissuing the key and
        /// «выйти на всех устройствах» do that (ТЗ 27, «Безопасность»).
        /// </summary>
        public Guid SessionStamp { get; private set; }

        public UserStatus Status { get; private set; }

        public bool TrialUsed { get; private set; }

        public DateTime? TrialStartedAt { get; private set; }

        /// <summary>The user blocked the bot: nothing is sent until they write to it again.</summary>
        public bool BotBlocked { get; private set; }

        public DateTime CreatedAt { get; private set; }

        public DateTime UpdatedAt { get; private set; }

        public bool IsBlocked => Status == UserStatus.Blocked;

        /// <summary>The pseudonym the panel knows this user by (ТЗ: «Минимум данных в панели»).</summary>
        public string PanelUsername => PanelUsernameFor(Id);

        /// <summary>
        /// <c>u</c> and at least two digits: Remnawave wants 3–36 characters, so <c>u1</c> would be refused
        /// (found by the contract tests). From id 10 on the name is plain <c>u&lt;id&gt;</c>.
        /// </summary>
        public static string PanelUsernameFor(long userId) => $"u{userId:D2}";

        /// <summary>
        /// Only the exact form <see cref="PanelUsernameFor"/> produces: <c>u+5</c>, <c>u 5</c> or <c>u005</c>
        /// made by hand in the panel must not be taken for the user 5 by reconciliation and webhooks.
        /// </summary>
        public static bool TryParsePanelUsername(string? username, out long userId)
        {
            userId = 0;
            if (username is not { Length: > 1 } || username[0] != 'u'
                || !long.TryParse(username.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
                || parsed <= 0 || PanelUsernameFor(parsed) != username)
            {
                return false;
            }

            userId = parsed;
            return true;
        }

        public static User CreateForTelegram(long telegramId, DateTime utcNow) => new()
        {
            TelegramId = telegramId,
            Status = UserStatus.Active,
            SessionStamp = Guid.NewGuid(),
            CreatedAt = utcNow,
            UpdatedAt = utcNow,
        };

        public static User CreateForWeb(string accountKeyHash, DateTime utcNow) => new()
        {
            AccountKeyHash = accountKeyHash,
            AccountKeyIssuedAt = utcNow,
            Status = UserStatus.Active,
            SessionStamp = Guid.NewGuid(),
            CreatedAt = utcNow,
            UpdatedAt = utcNow,
        };

        /// <summary>
        /// Replaces the account key. The previous key stops working immediately (FR-ACC-04) and every
        /// cabinet session signed in with it ends (ТЗ 27, «Безопасность»).
        /// </summary>
        public void SetAccountKey(string accountKeyHash, DateTime utcNow)
        {
            AccountKeyHash = accountKeyHash;
            AccountKeyIssuedAt = utcNow;
            RotateSessionStamp(utcNow);
        }

        /// <summary>Ends all cabinet sessions of the user (FR-WEB-12).</summary>
        public void RotateSessionStamp(DateTime utcNow)
        {
            SessionStamp = Guid.NewGuid();
            UpdatedAt = utcNow;
        }

        /// <summary>
        /// Merge rule 1 (ТЗ 21, решение 24.09): the Telegram account had neither a subscription nor payments,
        /// so its Telegram ID moves into this web account together with the trial mark. The caller deletes
        /// the Telegram account first: the Telegram ID is unique.
        /// </summary>
        public void TakeOverTelegram(User telegramAccount, DateTime utcNow)
        {
            TelegramId = telegramAccount.TelegramId;
            BotBlocked = telegramAccount.BotBlocked;
            if (telegramAccount.TrialUsed && !TrialUsed)
            {
                TrialUsed = true;
                TrialStartedAt = telegramAccount.TrialStartedAt;
            }
            UpdatedAt = utcNow;
        }

        /// <summary>
        /// Merge rule 2 (ТЗ 21, решение 24.09): the web account had neither a subscription nor payments, so
        /// its key and email move into this Telegram account. The key the user signs in with changes, so
        /// every other session ends. The caller deletes the web account first: key and email are unique.
        /// </summary>
        public void TakeOverWebCredentials(User webAccount, DateTime utcNow)
        {
            AccountKeyHash = webAccount.AccountKeyHash;
            AccountKeyIssuedAt = webAccount.AccountKeyIssuedAt;
            Email = webAccount.Email ?? Email;
            RotateSessionStamp(utcNow);
        }

        public void MarkTrialUsed(DateTime utcNow)
        {
            TrialUsed = true;
            TrialStartedAt = utcNow;
            UpdatedAt = utcNow;
        }

        public void MarkBotBlocked(DateTime utcNow)
        {
            if (BotBlocked)
            {
                return;
            }
            BotBlocked = true;
            UpdatedAt = utcNow;
        }

        public void MarkBotReachable(DateTime utcNow)
        {
            if (!BotBlocked)
            {
                return;
            }
            BotBlocked = false;
            UpdatedAt = utcNow;
        }

        public Result Block(DateTime utcNow)
        {
            if (IsBlocked)
            {
                return Result.Failure(UserErrors.AlreadyBlocked);
            }
            Status = UserStatus.Blocked;
            UpdatedAt = utcNow;
            return Result.Success();
        }

        public Result Unblock(DateTime utcNow)
        {
            if (!IsBlocked)
            {
                return Result.Failure(UserErrors.NotBlocked);
            }
            Status = UserStatus.Active;
            UpdatedAt = utcNow;
            return Result.Success();
        }
    }
}
