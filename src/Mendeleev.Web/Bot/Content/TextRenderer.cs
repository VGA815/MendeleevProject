using System.Globalization;
using System.Net;
using System.Text;
using Mendeleev.Domain.Common;

namespace Mendeleev.Web.Bot.Content
{
    /// <summary>
    /// Fills <c>{placeholders}</c> in a content template. Values are HTML-encoded: the templates are
    /// Telegram HTML, the values are data.
    /// </summary>
    public static class TextRenderer
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

        public static string Render(string template, params (string Key, object? Value)[] values)
        {
            var builder = new StringBuilder(template);
            foreach ((string key, object? value) in values)
            {
                builder.Replace("{" + key + "}", Encode(value));
            }
            return builder.ToString();
        }

        public static string Encode(object? value) => value switch
        {
            null => string.Empty,
            string s => WebUtility.HtmlEncode(s),
            DateTime dt => FormatDate(dt),
            decimal d => d.ToString("N0", Ru),
            IFormattable f => WebUtility.HtmlEncode(f.ToString(null, Ru)),
            _ => WebUtility.HtmlEncode(value.ToString() ?? string.Empty),
        };

        /// <summary>Moscow time, as every date in the ТЗ: <c>09.11.2026 12:00</c>.</summary>
        public static string FormatDate(DateTime utc) =>
            MoscowTime.FromUtc(utc).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

        public static string FormatBytes(long bytes)
        {
            const double Gb = 1024d * 1024 * 1024;
            const double Mb = 1024d * 1024;
            return bytes >= Gb
                ? (bytes / Gb).ToString("0.0", Ru) + " ГБ"
                : (bytes / Mb).ToString("0", Ru) + " МБ";
        }
    }
}
