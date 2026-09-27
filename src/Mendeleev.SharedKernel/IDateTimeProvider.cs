namespace Mendeleev.SharedKernel
{
    public interface IDateTimeProvider
    {
        /// <summary>Current time, <see cref="DateTimeKind.Utc"/>.</summary>
        DateTime UtcNow { get; }
    }
}
