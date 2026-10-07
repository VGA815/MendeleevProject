namespace Mendeleev.Domain.Subscriptions
{
    /// <summary>What the user should be told once the change has reached the panel.</summary>
    public enum SubscriptionNotice
    {
        None = 0,

        /// <summary>Trial, first payment or a payment after archiving: the message carries the link.</summary>
        AccessIssued = 1,

        PaymentSucceeded = 2,
        Compensated = 3,
        LinkReissued = 4,
        Expired = 5,
        TrafficExhausted = 6,

        /// <summary>Bonus days of a promo code were added (FR-SUB-15).</summary>
        PromoBonus = 7,

        /// <summary>A refund ended the access (FR-PAY-16).</summary>
        RefundEnded = 8,

        /// <summary>A refund of an erroneous payment took its days away (FR-PAY-16).</summary>
        RefundShortened = 9,
    }
}
