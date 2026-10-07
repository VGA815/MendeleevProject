namespace Mendeleev.Domain.Subscriptions
{
    public enum ExpiredReason
    {
        Time = 0,

        /// <summary>The trial used up its traffic limit.</summary>
        Traffic = 1,

        /// <summary>The payment was refunded (FR-PAY-16).</summary>
        Refund = 2,
    }
}
