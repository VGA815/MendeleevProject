namespace Mendeleev.Domain.Notifications
{
    public enum NotificationStatus
    {
        Pending = 0,
        Sent = 1,
        Failed = 2,

        /// <summary>Not sent on purpose: user blocked the bot, has no Telegram, or the reason went away.</summary>
        Skipped = 3,
    }
}
