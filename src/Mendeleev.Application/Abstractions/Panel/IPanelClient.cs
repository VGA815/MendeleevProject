namespace Mendeleev.Application.Abstractions.Panel
{
    /// <summary>
    /// The VPN panel behind an interface (FR-PNL-01), so that the own panel of stage 2 can replace
    /// Remnawave without touching the business logic.
    /// </summary>
    /// <remarks>
    /// Transient failures (network, timeouts, 5xx) surface as <see cref="PanelUnavailableException"/>;
    /// a request the panel rejects as invalid surfaces as <see cref="PanelContractException"/>.
    /// </remarks>
    public interface IPanelClient
    {
        /// <exception cref="PanelConflictException">A user with this username already exists.</exception>
        Task<PanelUser> CreateUserAsync(PanelUserSpec spec, CancellationToken cancellationToken);

        /// <summary>Pushes the full desired state. <see cref="PanelUserSpec.ExpireAt"/> is sent only if in the future.</summary>
        /// <exception cref="PanelUserNotFoundException">The user is gone from the panel.</exception>
        Task<PanelUser> UpdateUserAsync(int panelUserId, PanelUserSpec spec, CancellationToken cancellationToken);

        Task<PanelUser?> GetUserAsync(int panelUserId, CancellationToken cancellationToken);

        Task<PanelUser?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken);

        IAsyncEnumerable<PanelUser> ListUsersAsync(CancellationToken cancellationToken);

        /// <summary>Deleting a user that no longer exists is not an error.</summary>
        Task DeleteUserAsync(int panelUserId, CancellationToken cancellationToken);

        /// <summary>Old link stops working; the new short UUID is ours so the link stays reproducible.</summary>
        Task<PanelUser> RevokeSubscriptionAsync(int panelUserId, string newShortUuid, CancellationToken cancellationToken);

        Task<IReadOnlyList<PanelDevice>> GetDevicesAsync(int panelUserId, CancellationToken cancellationToken);

        Task DeleteDeviceAsync(int panelUserId, string hwid, CancellationToken cancellationToken);

        Task DeleteAllDevicesAsync(int panelUserId, CancellationToken cancellationToken);
    }

    /// <summary>The desired state of a panel user, computed from our subscription and tariff.</summary>
    public sealed record PanelUserSpec(
        string Username,
        string ShortUuid,
        Guid VlessUuid,
        DateTime ExpireAt,
        long TrafficLimitBytes,
        int HwidDeviceLimit,
        IReadOnlyList<Guid> InternalSquads,
        bool Enabled);

    public sealed record PanelUser(
        int Id,
        string Username,
        string ShortUuid,
        Guid VlessUuid,
        PanelUserStatus Status,
        DateTime ExpireAt,
        long TrafficLimitBytes,
        int? HwidDeviceLimit,
        IReadOnlyList<Guid> InternalSquads,
        string SubscriptionUrl,
        long UsedTrafficBytes,
        long LifetimeUsedTrafficBytes,
        DateTime? FirstConnectedAt);

    public enum PanelUserStatus
    {
        Active,
        Disabled,
        Limited,
        Expired,
        Unknown,
    }

    /// <summary>A device (HWID) registered by the client. The request IP is deliberately not carried over.</summary>
    public sealed record PanelDevice(
        string Hwid,
        string? Platform,
        string? OsVersion,
        string? DeviceModel,
        DateTime CreatedAt);

    public class PanelException(string message, Exception? innerException = null) : Exception(message, innerException);

    /// <summary>Network error, timeout, 5xx or an open circuit — worth retrying later.</summary>
    public sealed class PanelUnavailableException(string message, Exception? innerException = null)
        : PanelException(message, innerException);

    /// <summary>
    /// The panel rejected the request as invalid (4xx). Retrying will not help: this is a bug or an
    /// incompatible panel version (ТЗ 24, «Обработка ошибок»).
    /// </summary>
    public sealed class PanelContractException(string message) : PanelException(message), Abstractions.INonRetryableException;

    public sealed class PanelUserNotFoundException(int panelUserId)
        : PanelException($"Panel user {panelUserId} not found.");

    public sealed class PanelConflictException(string username)
        : PanelException($"Panel user '{username}' already exists.");
}
