using Microsoft.Extensions.Caching.Memory;

namespace Mendeleev.Web.Bot.Infrastructure
{
    /// <summary>What a staff member is typing right now (a reason, a broadcast text).</summary>
    public sealed record Conversation(string Kind, long? UserId = null, int? Days = null, string? Segment = null, bool Incident = false);

    public static class ConversationKinds
    {
        public const string ExtendReason = "extend_reason";
        public const string BlockReason = "block_reason";
        public const string BroadcastText = "broadcast_text";
    }

    /// <summary>
    /// Short-lived dialog state in memory. After a restart the staff member simply starts the action
    /// again; nothing of the user's correspondence is stored (ТЗ 26: «Бот не сохраняет содержимое переписки»).
    /// </summary>
    public sealed class ConversationStore(IMemoryCache cache)
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        public Conversation? Get(long chatId) => cache.Get<Conversation>(Key(chatId));

        public void Set(long chatId, Conversation conversation) => cache.Set(Key(chatId), conversation, Lifetime);

        public void Clear(long chatId) => cache.Remove(Key(chatId));

        /// <summary>HWIDs do not fit into 64-byte callback data, so the list is remembered and buttons carry an index.</summary>
        public void SetDevices(long chatId, long userId, IReadOnlyList<string> hwids) =>
            cache.Set($"devices:{chatId}:{userId}", hwids, Lifetime);

        public string? GetDevice(long chatId, long userId, int index) =>
            cache.Get<IReadOnlyList<string>>($"devices:{chatId}:{userId}") is { } list && index >= 0 && index < list.Count
                ? list[index]
                : null;

        private static string Key(long chatId) => $"conversation:{chatId}";
    }
}
