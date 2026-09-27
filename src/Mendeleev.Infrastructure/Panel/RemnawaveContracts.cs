using System.Text.Json.Serialization;
using Mendeleev.Application.Abstractions.Panel;

namespace Mendeleev.Infrastructure.Panel
{
    // DTOs for the pinned Remnawave 3.x contract (libs/contract in remnawave/backend): users are addressed by
    // numeric id, every response is wrapped in { "response": … }. Only the fields we use are declared.

    internal sealed record RemnawaveEnvelope<T>([property: JsonPropertyName("response")] T Response);

    internal sealed record RemnawaveUserDto(
        int Id,
        string Username,
        string ShortUuid,
        Guid VlessUuid,
        string Status,
        DateTime ExpireAt,
        long TrafficLimitBytes,
        int? HwidDeviceLimit,
        string? SubscriptionUrl,
        IReadOnlyList<RemnawaveSquadDto>? ActiveInternalSquads,
        RemnawaveUserTrafficDto? UserTraffic)
    {
        public PanelUser ToPanelUser() => new(
            Id,
            Username,
            ShortUuid,
            VlessUuid,
            RemnawaveStatuses.Parse(Status),
            DateTime.SpecifyKind(ExpireAt, DateTimeKind.Utc),
            TrafficLimitBytes,
            HwidDeviceLimit,
            ActiveInternalSquads?.Select(s => s.Uuid).ToList() ?? [],
            SubscriptionUrl ?? string.Empty,
            UserTraffic?.UsedTrafficBytes ?? 0,
            UserTraffic?.LifetimeUsedTrafficBytes ?? 0,
            UserTraffic?.FirstConnectedAt is DateTime first ? DateTime.SpecifyKind(first, DateTimeKind.Utc) : null);
    }

    internal sealed record RemnawaveSquadDto(Guid Uuid, string? Name);

    internal sealed record RemnawaveUserTrafficDto(
        long UsedTrafficBytes,
        long LifetimeUsedTrafficBytes,
        DateTime? OnlineAt,
        DateTime? FirstConnectedAt);

    internal sealed record RemnawaveUsersPageDto(IReadOnlyList<RemnawaveUserDto> Users, int Total);

    internal sealed record RemnawaveDevicesDto(int Total, IReadOnlyList<RemnawaveDeviceDto> Devices);

    internal sealed record RemnawaveDeviceDto(
        string Hwid,
        int UserId,
        string? Platform,
        string? OsVersion,
        string? DeviceModel,
        DateTime CreatedAt);

    internal sealed record CreateUserRequest(
        string Username,
        string Status,
        string ShortUuid,
        Guid VlessUuid,
        long TrafficLimitBytes,
        string TrafficLimitStrategy,
        DateTime ExpireAt,
        int HwidDeviceLimit,
        IReadOnlyList<Guid> ActiveInternalSquads);

    /// <remarks><c>expireAt</c> must be in the future for the panel, so it is omitted otherwise.</remarks>
    internal sealed record UpdateUserRequest(
        int Id,
        string Status,
        long TrafficLimitBytes,
        string TrafficLimitStrategy,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? ExpireAt,
        int HwidDeviceLimit,
        IReadOnlyList<Guid> ActiveInternalSquads);

    internal sealed record RevokeSubscriptionRequest(string ShortUuid, bool RevokeOnlyPasswords);

    internal sealed record UserIdRequest(int UserId);

    internal sealed record DeleteDeviceRequest(int UserId, string Hwid);

    /// <summary>Webhook envelope: <c>scope</c>, <c>event</c>, <c>timestamp</c>, <c>data</c>.</summary>
    internal sealed record RemnawaveWebhookDto(
        string Scope,
        string Event,
        DateTime Timestamp,
        System.Text.Json.JsonElement Data);

    internal static class RemnawaveStatuses
    {
        public const string Active = "ACTIVE";
        public const string Disabled = "DISABLED";
        public const string NoReset = "NO_RESET";

        public static PanelUserStatus Parse(string? status) => status switch
        {
            "ACTIVE" => PanelUserStatus.Active,
            "DISABLED" => PanelUserStatus.Disabled,
            "LIMITED" => PanelUserStatus.Limited,
            "EXPIRED" => PanelUserStatus.Expired,
            _ => PanelUserStatus.Unknown,
        };
    }
}
