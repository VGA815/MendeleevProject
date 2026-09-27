namespace Mendeleev.Web.Bot.Content
{
    /// <summary>
    /// Texts, buttons, FAQ and instructions of the bot. They live in <c>content/bot.yaml</c>, which is
    /// mounted into the container and re-read on change without a restart (FR-BOT-17, решение 24.09):
    /// app store links and names change often, especially for iOS, and are not worth a release.
    /// </summary>
    public sealed class BotContent
    {
        public const string SectionName = "BotContent";

        public Dictionary<string, string> Texts { get; init; } = [];

        public Dictionary<string, string> Buttons { get; init; } = [];

        /// <summary>By <see cref="Domain.Notifications.NotificationKind"/> name.</summary>
        public Dictionary<string, string> Notifications { get; init; } = [];

        public List<PlatformInstruction> Platforms { get; init; } = [];

        public List<FaqItem> Faq { get; init; } = [];

        /// <summary>Support in Telegram: an account or a group of the owner's people.</summary>
        public string SupportUrl { get; init; } = string.Empty;

        /// <summary>Support outside Telegram (email), shown when Telegram may be unavailable.</summary>
        public string SupportEmail { get; init; } = string.Empty;

        /// <summary>
        /// Where «Добавить в Happ» leads. Telegram URL buttons open only http(s) and tg:// links, so this is
        /// an https address on the subscriptions domain; <c>{url}</c> is the subscription link (ТЗ 26).
        /// </summary>
        public string ImportUrlTemplate { get; init; } = "{url}";

        /// <summary>FAQ entry shown by the «Как обновить подписку» button of incident messages.</summary>
        public string UpdateSubscriptionFaqId { get; init; } = "update";

        public string Text(string key) =>
            Texts.TryGetValue(key, out string? value) ? value : $"[{key}]";

        public string Button(string key) =>
            Buttons.TryGetValue(key, out string? value) ? value : key;
    }

    public sealed class PlatformInstruction
    {
        public string Id { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        /// <summary>Telegram HTML. <c>{url}</c> is the subscription link.</summary>
        public string Instruction { get; init; } = string.Empty;
    }

    public sealed class FaqItem
    {
        public string Id { get; init; } = string.Empty;

        public string Question { get; init; } = string.Empty;

        /// <summary>Telegram HTML.</summary>
        public string Answer { get; init; } = string.Empty;
    }
}
