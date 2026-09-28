using System.Security.Cryptography;

namespace Mendeleev.Domain.Users
{
    /// <summary>
    /// A one-time code (ТЗ 12, «LinkCode»): linking Telegram to a web account (FR-ACC-09) and, from
    /// stage 1.5, the sign-in link sent by email. Only the HMAC of the code is stored.
    /// </summary>
    public sealed class LinkCode
    {
        public const int TelegramCodeLength = 8;

        /// <summary>ТЗ 21, «Бизнес-правила»: the linking code lives 10 minutes and works once.</summary>
        public static readonly TimeSpan TelegramCodeLifetime = TimeSpan.FromMinutes(10);

        private LinkCode() { }

        public long Id { get; private set; }

        /// <summary>For <see cref="LinkCodePurpose.LinkTelegram"/> — the account that has the Telegram ID.</summary>
        public long UserId { get; private set; }

        public LinkCodePurpose Purpose { get; private set; }

        public string CodeHash { get; private set; } = string.Empty;

        public DateTime ExpiresAt { get; private set; }

        public DateTime? UsedAt { get; private set; }

        public DateTime CreatedAt { get; private set; }

        public static LinkCode ForTelegram(long telegramUserId, string codeHash, DateTime utcNow) => new()
        {
            UserId = telegramUserId,
            Purpose = LinkCodePurpose.LinkTelegram,
            CodeHash = codeHash,
            ExpiresAt = utcNow + TelegramCodeLifetime,
            CreatedAt = utcNow,
        };

        public bool IsUsable(DateTime utcNow) => UsedAt is null && utcNow < ExpiresAt;

        public void MarkUsed(DateTime utcNow) => UsedAt ??= utcNow;

        /// <summary>8 random digits from a cryptographic generator.</summary>
        public static string GenerateTelegramCode()
        {
            Span<char> digits = stackalloc char[TelegramCodeLength];
            for (int i = 0; i < TelegramCodeLength; i++)
            {
                digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
            }
            return new string(digits);
        }

        /// <summary>Accepts the code with spaces or dashes, as people retype it from the bot.</summary>
        public static bool TryNormalizeTelegramCode(string? input, out string code)
        {
            code = string.Empty;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            Span<char> buffer = stackalloc char[TelegramCodeLength];
            int count = 0;
            foreach (char c in input)
            {
                if (c is ' ' or '-' or ' ' or '\t')
                {
                    continue;
                }
                if (c is < '0' or > '9' || count == TelegramCodeLength)
                {
                    return false;
                }
                buffer[count++] = c;
            }

            if (count != TelegramCodeLength)
            {
                return false;
            }

            code = new string(buffer);
            return true;
        }
    }

    public enum LinkCodePurpose
    {
        LinkTelegram = 0,
        EmailLogin = 1,
    }
}
