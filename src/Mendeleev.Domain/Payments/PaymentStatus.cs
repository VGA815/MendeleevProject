namespace Mendeleev.Domain.Payments
{
    /// <summary>ТЗ 23, «Статусы платежа».</summary>
    public enum PaymentStatus
    {
        Created = 0,
        Pending = 1,
        Succeeded = 2,
        Canceled = 3,
        Failed = 4,
        Refunded = 5,
    }
}
