namespace Mendeleev.Domain.Subscriptions
{
    /// <summary>ТЗ 22, «Жизненный цикл подписки».</summary>
    public enum SubscriptionStatus
    {
        Trial = 0,
        Active = 1,
        Expired = 2,
        Disabled = 3,
        Archived = 4,
    }
}
