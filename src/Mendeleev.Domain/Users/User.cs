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

        public static bool TryParsePanelUsername(string? username, out long userId)
        {
            userId = 0;
            return username is { Length: > 1 } && username[0] == 'u'
                && long.TryParse(username.AsSpan(1), out userId) && userId > 0;
        }

        public static User CreateForTelegram(long telegramId, DateTime utcNow) => new()
        {
            TelegramId = telegramId,
            Status = UserStatus.Active,
            CreatedAt = utcNow,
            UpdatedAt = utcNow,
        };

        public static User CreateForWeb(string accountKeyHash, DateTime utcNow) => new()
        {
            AccountKeyHash = accountKeyHash,
            AccountKeyIssuedAt = utcNow,
            Status = UserStatus.Active,
            CreatedAt = utcNow,
            UpdatedAt = utcNow,
        };

        /// <summary>Replaces the account key. The previous key stops working immediately (FR-ACC-04).</summary>
        public void SetAccountKey(string accountKeyHash, DateTime utcNow)
        {
            AccountKeyHash = accountKeyHash;
            AccountKeyIssuedAt = utcNow;
            UpdatedAt = utcNow;
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
