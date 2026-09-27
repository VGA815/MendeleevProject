namespace Mendeleev.Domain.Broadcasts
{
    public enum BroadcastSegment
    {
        All = 0,

        /// <summary>Paid and active.</summary>
        Active = 1,

        Trial = 2,

        /// <summary>Expired within the last 30 days (status «expired», not yet archived).</summary>
        Expired = 3,

        NoSubscription = 4,
    }
}
