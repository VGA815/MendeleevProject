using System.Security.Cryptography;

namespace Mendeleev.Domain.Users
{
    /// <summary>
    /// The account key for signing in on the site when Telegram is unavailable: 16 random digits shown
    /// in groups of four, like a Mullvad account number (ТЗ 21, решение 24.09). ~53 bits of entropy
    /// together with the attempt limits make brute force impractical.
    /// </summary>
    public readonly record struct AccountKey
    {
        public const int Length = 16;

        private AccountKey(string digits)
        {
            Digits = digits;
        }

        /// <summary>The normalized form: exactly 16 digits, no separators. This is what gets hashed.</summary>
        public string Digits { get; }

        public static AccountKey Generate()
        {
            Span<char> digits = stackalloc char[Length];
            for (int i = 0; i < Length; i++)
            {
                digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
            }
            return new AccountKey(new string(digits));
        }

        /// <summary>Accepts the key with or without spaces and dashes (ТЗ 21, «Бизнес-правила»).</summary>
        public static bool TryParse(string? input, out AccountKey key)
        {
            key = default;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            Span<char> buffer = stackalloc char[Length];
            int count = 0;
            foreach (char c in input)
            {
                if (c is ' ' or '-' or ' ' or '\t')
                {
                    continue;
                }
                if (c is < '0' or > '9' || count == Length)
                {
                    return false;
                }
                buffer[count++] = c;
            }

            if (count != Length)
            {
                return false;
            }

            key = new AccountKey(new string(buffer));
            return true;
        }

        /// <summary><c>1234 5678 9012 3456</c> — the way the key is shown to the user.</summary>
        public string ToDisplayString() =>
            $"{Digits[..4]} {Digits[4..8]} {Digits[8..12]} {Digits[12..]}";

        public override string ToString() => "****";
    }
}
