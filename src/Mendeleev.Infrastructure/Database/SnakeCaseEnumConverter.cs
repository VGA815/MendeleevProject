using System.Collections.Frozen;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Mendeleev.Infrastructure.Database
{
    /// <summary>
    /// Stores an enum as snake_case text: <c>SubscriptionStatus.Active</c> → <c>active</c>,
    /// <c>NotificationKind.Expiry3d</c> → <c>expiry_3d</c>. Renaming an enum member is a data migration.
    /// </summary>
    internal sealed class SnakeCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
        value => ToProvider(value),
        text => FromProvider(text))
        where TEnum : struct, Enum
    {
        private static readonly FrozenDictionary<TEnum, string> ToDb =
            Enum.GetValues<TEnum>().ToFrozenDictionary(v => v, v => ToSnakeCase(v.ToString()));

        private static readonly FrozenDictionary<string, TEnum> FromDb =
            Enum.GetValues<TEnum>().ToFrozenDictionary(v => ToSnakeCase(v.ToString()), v => v, StringComparer.Ordinal);

        public static string ToProvider(TEnum value) => ToDb[value];

        public static TEnum FromProvider(string text) =>
            FromDb.TryGetValue(text, out TEnum value)
                ? value
                : throw new InvalidOperationException($"Unknown {typeof(TEnum).Name} value '{text}' in the database.");

        public static string ToSnakeCase(string name)
        {
            var builder = new StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool boundary = i > 0 && (
                    char.IsUpper(c) && !char.IsUpper(name[i - 1])
                    || char.IsDigit(c) && !char.IsDigit(name[i - 1])
                    || char.IsUpper(c) && i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]));

                if (boundary)
                {
                    builder.Append('_');
                }
                builder.Append(char.ToLowerInvariant(c));
            }
            return builder.ToString();
        }
    }
}
