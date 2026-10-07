using Microsoft.Extensions.Caching.Memory;

namespace Mendeleev.Web.Bot.Infrastructure
{
    /// <summary>What a staff member is typing right now (a reason, a broadcast text, a manual payment).</summary>
    public sealed record Conversation(
        string Kind,
        long? UserId = null,
        int? Days = null,
        string? Segment = null,
        bool Incident = false,
        string? Tariff = null,
        Guid? PaymentId = null,
        string? Option = null);

    public static class ConversationKinds
    {
        public const string ExtendReason = "extend_reason";
        public const string BlockReason = "block_reason";
        public const string BroadcastText = "broadcast_text";
        public const string ManualPayment = "manual_payment";
        public const string RefundReason = "refund_reason";
        public const string CompensationWindow = "compensation_window";
    }

    /// <summary>An action waiting for its «Подтвердить» button; the button carries the token.</summary>
    public interface IStaffDraft
    {
        string Token { get; }
    }

    /// <summary>A payment taken outside the system, waiting for the admin's confirmation; the button carries the token.</summary>
    public sealed record ManualPaymentDraft(string Token, long UserId, string TariffCode, decimal Amount, string Comment);

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

        /// <summary>A user pressed «Ввести промокод»: their next plain message is the code. Only the fact is kept.</summary>
        public void SetPromoPrompt(long chatId) => cache.Set(PromoPromptKey(chatId), true, Lifetime);

        /// <returns>True once, if the user was asked for a code.</returns>
        public bool TakePromoPrompt(long chatId)
        {
            if (!cache.TryGetValue(PromoPromptKey(chatId), out _))
            {
                return false;
            }

            cache.Remove(PromoPromptKey(chatId));
            return true;
        }

        public void ClearPromoPrompt(long chatId) => cache.Remove(PromoPromptKey(chatId));

        public void SetManualPayment(long chatId, ManualPaymentDraft draft) => cache.Set(ManualPaymentKey(chatId), draft, Lifetime);

        /// <summary>One draft of each kind per chat; a newer one replaces the older.</summary>
        public void SetDraft<T>(long chatId, T draft)
            where T : class, IStaffDraft =>
            cache.Set(DraftKey<T>(chatId), draft, Lifetime);

        /// <summary>The draft behind a button that changes it rather than confirms it.</summary>
        public T? GetDraft<T>(long chatId, string token)
            where T : class, IStaffDraft =>
            cache.Get<T>(DraftKey<T>(chatId)) is { } draft && draft.Token == token ? draft : null;

        /// <summary>Gives the draft out once, and only to the button it was shown with (see <see cref="TakeManualPayment"/>).</summary>
        public T? TakeDraft<T>(long chatId, string token)
            where T : class, IStaffDraft
        {
            if (cache.Get<T>(DraftKey<T>(chatId)) is not { } draft || draft.Token != token)
            {
                return null;
            }

            cache.Remove(DraftKey<T>(chatId));
            return draft;
        }

        /// <summary>
        /// Gives the draft out once, and only to the button it was shown with: a second tap or an old button
        /// finds nothing. Updates of one chat are handled one by one, so this cannot race with itself.
        /// </summary>
        public ManualPaymentDraft? TakeManualPayment(long chatId, long userId, string token)
        {
            if (cache.Get<ManualPaymentDraft>(ManualPaymentKey(chatId)) is not { } draft || draft.UserId != userId || draft.Token != token)
            {
                return null;
            }

            cache.Remove(ManualPaymentKey(chatId));
            return draft;
        }

        private static string Key(long chatId) => $"conversation:{chatId}";

        private static string ManualPaymentKey(long chatId) => $"manual-payment:{chatId}";

        private static string PromoPromptKey(long chatId) => $"promo-prompt:{chatId}";

        private static string DraftKey<T>(long chatId) => $"draft:{typeof(T).Name}:{chatId}";
    }
}
