using Mendeleev.Web.Bot.Content;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Mendeleev.Web.Bot
{
    /// <summary>Callback data values. Telegram limits them to 64 bytes.</summary>
    internal static class Cb
    {
        public const string Menu = "menu";
        public const string Subscription = "sub";
        public const string Buy = "buy";
        public const string Tariff = "t:";
        public const string Pay = "pay:";
        public const string Check = "chk:";
        public const string Trial = "trial";
        public const string Connect = "con";
        public const string Platform = "con:";
        public const string Devices = "dev";
        public const string ResetDevicesAsk = "devr";
        public const string ResetDevicesDo = "devr!";
        public const string Help = "help";
        public const string Faq = "faq:";
        public const string Key = "key";
        public const string KeyIssue = "key!";
        public const string Qr = "qr";
        public const string Noop = "nop";
    }

    internal static class Keyboards
    {
        public static InlineKeyboardButton Callback(string text, string data) => InlineKeyboardButton.WithCallbackData(text, data);

        public static InlineKeyboardButton Url(string text, string url) => InlineKeyboardButton.WithUrl(text, url);

        public static InlineKeyboardButton Copy(string text, string value) =>
            InlineKeyboardButton.WithCopyText(text, new CopyTextButton { Text = value });

        public static InlineKeyboardMarkup Of(params IEnumerable<InlineKeyboardButton>[] rows) =>
            new(rows.Where(r => r.Any()).Select(r => r.ToArray()));

        public static InlineKeyboardMarkup Of(IEnumerable<IEnumerable<InlineKeyboardButton>> rows) =>
            new(rows.Where(r => r.Any()).Select(r => r.ToArray()));

        public static InlineKeyboardMarkup MainMenu(BotContent c, bool trialAvailable)
        {
            var rows = new List<InlineKeyboardButton[]>
            {
                new[] { Callback(c.Button("MySubscription"), Cb.Subscription) },
                new[] { Callback(c.Button("Buy"), Cb.Buy) },
            };
            if (trialAvailable)
            {
                rows.Add([Callback(c.Button("Trial"), Cb.Trial)]);
            }
            rows.Add([Callback(c.Button("Connect"), Cb.Connect), Callback(c.Button("Devices"), Cb.Devices)]);
            rows.Add([Callback(c.Button("Help"), Cb.Help), Callback(c.Button("Key"), Cb.Key)]);
            return new InlineKeyboardMarkup(rows);
        }

        public static InlineKeyboardButton[] BackToMenu(BotContent c) => [Callback(c.Button("Menu"), Cb.Menu)];

        public static IEnumerable<InlineKeyboardButton[]> Platforms(BotContent c) =>
            c.Platforms.Chunk(2).Select(pair => pair.Select(p => Callback(p.Title, Cb.Platform + p.Id)).ToArray());

        /// <summary>The subscription link itself, or a redirect page on the subscriptions domain that opens Happ.</summary>
        public static string ImportUrl(BotContent c, string subscriptionUrl) =>
            c.ImportUrlTemplate == "{url}"
                ? subscriptionUrl
                : c.ImportUrlTemplate.Replace("{url}", Uri.EscapeDataString(subscriptionUrl), StringComparison.Ordinal);

        public static InlineKeyboardButton[] Support(BotContent c) =>
            string.IsNullOrWhiteSpace(c.SupportUrl) ? [] : [Url(c.Button("Support"), c.SupportUrl)];
    }
}
