using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mendeleev.Infrastructure.Payments.Lava
{
    /// <summary>
    /// Lava's signatures are hex HMAC-SHA256. Our requests: over the exact body bytes with the secret key, in the
    /// <c>Signature</c> header (developer.lava.ru, «Security»). Webhooks: with the additional key (FR-PAY-02).
    /// </summary>
    /// <remarks>
    /// The documentation says a webhook is signed over its body. Lava's PHP backend in fact signs the body as it
    /// re-encodes it — top-level keys sorted, <c>json_encode</c> escaping: that is what the official SDK checks
    /// (lava-payment/lava, <c>ClientCheckSignatureWebhook</c>) and its test vector matches only that form. Working
    /// integrations see both forms, so both are accepted: either one is an HMAC of the whole content with a key only
    /// Lava and we have.
    /// </remarks>
    internal static class LavaSignature
    {
        public static string Sign(ReadOnlySpan<byte> data, string key) =>
            Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), data));

        public static bool IsValidWebhook(byte[] body, string signature, string key)
        {
            byte[] expected;
            try
            {
                expected = Convert.FromHexString(signature.Trim());
            }
            catch (FormatException)
            {
                return false;
            }

            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            if (CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(keyBytes, body), expected))
            {
                return true;
            }

            string? canonical = PhpCanonicalJson(body);
            return canonical is not null
                && CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes(canonical)), expected);
        }

        /// <summary>The body as PHP prints it after <c>json_decode</c>, <c>ksort</c> and <c>json_encode</c>; null if it is not a JSON object.</summary>
        internal static string? PhpCanonicalJson(byte[] body)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var builder = new StringBuilder(body.Length + 64);
                WriteObject(builder, document.RootElement.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal));
                return builder.ToString();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static void WriteObject(StringBuilder builder, IEnumerable<JsonProperty> properties)
        {
            builder.Append('{');
            bool first = true;
            foreach (JsonProperty property in properties)
            {
                if (!first)
                {
                    builder.Append(',');
                }
                first = false;

                WriteString(builder, property.Name);
                builder.Append(':');
                WriteValue(builder, property.Value);
            }
            builder.Append('}');
        }

        private static void WriteValue(StringBuilder builder, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    WriteObject(builder, value.EnumerateObject());
                    break;

                case JsonValueKind.Array:
                    builder.Append('[');
                    bool first = true;
                    foreach (JsonElement item in value.EnumerateArray())
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }
                        first = false;
                        WriteValue(builder, item);
                    }
                    builder.Append(']');
                    break;

                case JsonValueKind.String:
                    WriteString(builder, value.GetString()!);
                    break;

                case JsonValueKind.Number:
                    WriteNumber(builder, value);
                    break;

                default:
                    builder.Append(value.GetRawText());
                    break;
            }
        }

        /// <summary>PHP decodes <c>100.0</c> to a float and prints it back as <c>100</c>, <c>1.50</c> as <c>1.5</c>.</summary>
        private static void WriteNumber(StringBuilder builder, JsonElement value)
        {
            if (value.TryGetInt64(out long integer))
            {
                builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                return;
            }

            double number = value.GetDouble();
            builder.Append(Math.Abs(number) < 1e15 && number == Math.Floor(number)
                ? ((long)number).ToString(CultureInfo.InvariantCulture)
                : number.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary><c>json_encode</c> without flags: escapes <c>/</c> and everything outside ASCII as lowercase <c>\uXXXX</c>.</summary>
        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '/': builder.Append("\\/"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7F)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }
            builder.Append('"');
        }
    }
}
