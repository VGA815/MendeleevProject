namespace Mendeleev.Domain.Payments
{
    /// <summary>
    /// What an admin's refund mark does to the subscription (FR-PAY-16; решение 07.10 — как в оферте, раздел 6).
    /// </summary>
    public enum RefundKind
    {
        /// <summary>The user gave up the service and got the unused days back (п. 6.3): access ends now.</summary>
        UnusedDays = 0,

        /// <summary>An erroneous or duplicate payment returned in full (п. 6.2): the term loses its days.</summary>
        Erroneous = 1,
    }
}
