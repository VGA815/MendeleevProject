using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mendeleev.Application.Abstractions.Panel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Mendeleev.Infrastructure.Panel
{
    /// <summary>
    /// <see cref="IPanelClient"/> over the Remnawave REST API (no official .NET SDK — ТЗ 24, «API и
    /// контракты»). The panel is abroad, so transient failures are expected: they surface as
    /// <see cref="PanelUnavailableException"/> and the outbox retries.
    /// </summary>
    internal sealed class RemnawaveClient(
        HttpClient http,
        IOptions<RemnawaveOptions> options,
        ILogger<RemnawaveClient> logger)
        : IPanelClient
    {
        internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        public async Task<PanelUser> CreateUserAsync(PanelUserSpec spec, CancellationToken cancellationToken)
        {
            var request = new CreateUserRequest(
                spec.Username,
                spec.Enabled ? RemnawaveStatuses.Active : RemnawaveStatuses.Disabled,
                spec.ShortUuid,
                spec.VlessUuid,
                spec.TrafficLimitBytes,
                RemnawaveStatuses.NoReset,
                DateTime.SpecifyKind(spec.ExpireAt, DateTimeKind.Utc),
                spec.HwidDeviceLimit,
                spec.InternalSquads);

            using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "api/users", request, cancellationToken);

            if (response.StatusCode is HttpStatusCode.Conflict || await IsAlreadyExistsAsync(response, cancellationToken))
            {
                throw new PanelConflictException(spec.Username);
            }

            return (await ReadAsync<RemnawaveUserDto>(response, "create user", cancellationToken)).ToPanelUser();
        }

        public async Task<PanelUser> UpdateUserAsync(int panelUserId, PanelUserSpec spec, CancellationToken cancellationToken)
        {
            // The panel accepts only a future expireAt; for a past date it expires the user by itself.
            DateTime? expireAt = spec.ExpireAt > DateTime.UtcNow.AddMinutes(1)
                ? DateTime.SpecifyKind(spec.ExpireAt, DateTimeKind.Utc)
                : null;

            var request = new UpdateUserRequest(
                panelUserId,
                spec.Enabled ? RemnawaveStatuses.Active : RemnawaveStatuses.Disabled,
                spec.TrafficLimitBytes,
                RemnawaveStatuses.NoReset,
                expireAt,
                spec.HwidDeviceLimit,
                spec.InternalSquads);

            using HttpResponseMessage response = await SendAsync(HttpMethod.Patch, "api/users", request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new PanelUserNotFoundException(panelUserId);
            }

            return (await ReadAsync<RemnawaveUserDto>(response, "update user", cancellationToken)).ToPanelUser();
        }

        public async Task<PanelUser?> GetUserAsync(int panelUserId, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"api/users/{panelUserId}", null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            return (await ReadAsync<RemnawaveUserDto>(response, "get user", cancellationToken)).ToPanelUser();
        }

        public async Task<PanelUser?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"api/users/by-username/{Uri.EscapeDataString(username)}", null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            return (await ReadAsync<RemnawaveUserDto>(response, "get user by username", cancellationToken)).ToPanelUser();
        }

        public async IAsyncEnumerable<PanelUser> ListUsersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            int pageSize = options.Value.PageSize;
            int start = 0;

            while (true)
            {
                using HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"api/users?start={start}&size={pageSize}", null, cancellationToken);
                RemnawaveUsersPageDto page = await ReadAsync<RemnawaveUsersPageDto>(response, "list users", cancellationToken);

                foreach (RemnawaveUserDto user in page.Users)
                {
                    yield return user.ToPanelUser();
                }

                start += page.Users.Count;
                if (page.Users.Count == 0 || start >= page.Total)
                {
                    yield break;
                }
            }
        }

        public async Task DeleteUserAsync(int panelUserId, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Delete, $"api/users/{panelUserId}", null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            await EnsureSuccessAsync(response, "delete user", cancellationToken);
        }

        public async Task<PanelUser> RevokeSubscriptionAsync(int panelUserId, string newShortUuid, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAsync(
                HttpMethod.Post,
                $"api/users/{panelUserId}/actions/revoke-subscription",
                new RevokeSubscriptionRequest(newShortUuid, RevokeOnlyPasswords: false),
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new PanelUserNotFoundException(panelUserId);
            }

            return (await ReadAsync<RemnawaveUserDto>(response, "revoke subscription", cancellationToken)).ToPanelUser();
        }

        public async Task<IReadOnlyList<PanelDevice>> GetDevicesAsync(int panelUserId, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"api/hwid/devices/{panelUserId}", null, cancellationToken);
            RemnawaveDevicesDto devices = await ReadAsync<RemnawaveDevicesDto>(response, "get devices", cancellationToken);

            // requestIp and userAgent are dropped here on purpose: we do not keep IPs (ТЗ 24).
            return devices.Devices
                .Select(d => new PanelDevice(d.Hwid, d.Platform, d.OsVersion, d.DeviceModel, DateTime.SpecifyKind(d.CreatedAt, DateTimeKind.Utc)))
                .ToList();
        }

        public async Task DeleteDeviceAsync(int panelUserId, string hwid, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "api/hwid/devices/delete", new DeleteDeviceRequest(panelUserId, hwid), cancellationToken);
            await EnsureSuccessAsync(response, "delete device", cancellationToken);
        }

        public async Task DeleteAllDevicesAsync(int panelUserId, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "api/hwid/devices/delete-all", new UserIdRequest(panelUserId), cancellationToken);
            await EnsureSuccessAsync(response, "delete all devices", cancellationToken);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            }

            try
            {
                return await http.SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
                                           or TaskCanceledException { InnerException: TimeoutException })
            {
                throw new PanelUnavailableException($"Panel request {method} {path} failed: {ex.Message}", ex);
            }
        }

        private async Task<T> ReadAsync<T>(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
        {
            await EnsureSuccessAsync(response, operation, cancellationToken);

            RemnawaveEnvelope<T>? envelope = await response.Content.ReadFromJsonAsync<RemnawaveEnvelope<T>>(JsonOptions, cancellationToken);
            return envelope is { Response: not null }
                ? envelope.Response
                : throw new PanelContractException($"Panel returned an empty body for {operation}.");
        }

        private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            string body = await SafeReadAsync(response, cancellationToken);
            int status = (int)response.StatusCode;

            if (status >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout)
            {
                throw new PanelUnavailableException($"Panel {operation} returned {status}: {body}");
            }

            logger.LogError("Panel rejected {Operation} with {Status}: {Body}", operation, status, body);
            throw new PanelContractException($"Panel {operation} returned {status}: {body}");
        }

        private static async Task<bool> IsAlreadyExistsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.StatusCode != HttpStatusCode.BadRequest)
            {
                return false;
            }

            string body = await SafeReadAsync(response, cancellationToken);
            return body.Contains("already exist", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                await response.Content.LoadIntoBufferAsync(cancellationToken);
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                return body.Length > 500 ? body[..500] : body;
            }
            catch (HttpRequestException)
            {
                return string.Empty;
            }
        }
    }
}
