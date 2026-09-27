namespace Mendeleev.Application.Abstractions.Alerts
{
    /// <summary>
    /// Alerts to the tech admin (and the admin where the ТЗ says so): Telegram alert bot plus a second
    /// channel in case Telegram is down (ТЗ 30, «Алерты»). Never throws.
    /// </summary>
    public interface IAlertSink
    {
        /// <summary>Raises the alert; the same <see cref="Alert.Key"/> is not repeated within the quiet window.</summary>
        Task RaiseAsync(Alert alert, CancellationToken cancellationToken = default);

        /// <summary>Raises only when the key occurred at least <paramref name="threshold"/> times within the window.</summary>
        Task RaiseOnSeriesAsync(Alert alert, int threshold, TimeSpan window, CancellationToken cancellationToken = default);
    }

    public sealed record Alert(AlertSeverity Severity, string Key, string Text, AlertAudience Audience = AlertAudience.TechAdmin);

    public enum AlertSeverity
    {
        Info,
        Warning,
        Critical,
    }

    public enum AlertAudience
    {
        TechAdmin,

        /// <summary>Tech admin and admin (owner).</summary>
        TechAdminAndAdmin,
    }
}
