using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Mendeleev.Application.Abstractions.Panel;

namespace Mendeleev.Infrastructure.Panel
{
    /// <summary>
    /// A panel that lives in memory: for running the bot locally without Remnawave and for tests
    /// (<c>Remnawave:UseInMemory = true</c>). Mimics the contract that matters: ids, short UUIDs, the
    /// "future expireAt only" rule, conflicts on username, devices.
    /// </summary>
    public sealed class InMemoryPanelClient : IPanelClient
    {
        private readonly ConcurrentDictionary<int, PanelUser> _users = new();
        private readonly ConcurrentDictionary<int, List<PanelDevice>> _devices = new();
        private readonly string _subscriptionBaseUrl;
        private int _nextId;

        public InMemoryPanelClient(string subscriptionBaseUrl = "https://sub.localhost")
        {
            _subscriptionBaseUrl = subscriptionBaseUrl.TrimEnd('/');

            // The local database outlives the process: ids from the start time do not collide with the
            // panel_user_id values a previous run left there (the column is unique).
            _nextId = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 1_000_000) * 1_000;
        }

        /// <summary>Makes every call fail as if the panel were unreachable.</summary>
        public bool IsDown { get; set; }

        public IReadOnlyCollection<PanelUser> Users => _users.Values.ToList();

        public Task<PanelUser> CreateUserAsync(PanelUserSpec spec, CancellationToken cancellationToken)
        {
            ThrowIfDown();
            if (_users.Values.Any(u => u.Username == spec.Username))
            {
                throw new PanelConflictException(spec.Username);
            }

            int id = Interlocked.Increment(ref _nextId);
            var user = new PanelUser(
                id, spec.Username, spec.ShortUuid, spec.VlessUuid,
                spec.Enabled ? PanelUserStatus.Active : PanelUserStatus.Disabled,
                spec.ExpireAt, spec.TrafficLimitBytes, spec.HwidDeviceLimit, spec.InternalSquads,
                $"{_subscriptionBaseUrl}/{spec.ShortUuid}", 0, 0, null);
            _users[id] = user;
            return Task.FromResult(user);
        }

        public Task<PanelUser> UpdateUserAsync(int panelUserId, PanelUserSpec spec, CancellationToken cancellationToken)
        {
            ThrowIfDown();
            if (!_users.TryGetValue(panelUserId, out PanelUser? user))
            {
                throw new PanelUserNotFoundException(panelUserId);
            }

            DateTime expireAt = spec.ExpireAt > DateTime.UtcNow.AddMinutes(1) ? spec.ExpireAt : user.ExpireAt;
            PanelUserStatus status = !spec.Enabled
                ? PanelUserStatus.Disabled
                : expireAt <= DateTime.UtcNow ? PanelUserStatus.Expired : PanelUserStatus.Active;

            user = user with
            {
                Status = status,
                ExpireAt = expireAt,
                TrafficLimitBytes = spec.TrafficLimitBytes,
                HwidDeviceLimit = spec.HwidDeviceLimit,
                InternalSquads = spec.InternalSquads,
            };
            _users[panelUserId] = user;
            return Task.FromResult(user);
        }

        public Task<PanelUser?> GetUserAsync(int panelUserId, CancellationToken cancellationToken)
        {
            ThrowIfDown();
            return Task.FromResult(_users.GetValueOrDefault(panelUserId));
        }

        public Task<PanelUser?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken)
        {
            ThrowIfDown();
            return Task.FromResult(_users.Values.FirstOrDefault(u => u.Username == username));
        }

        public async IAsyncEnumerable<PanelUser> ListUsersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ThrowIfDown();
            foreach (PanelUser user in _users.Values.OrderBy(u => u.Id))
            {
                yield return user;
            }
            await Task.CompletedTask;
        }

        public Task DeleteUserAsync(int panelUserId, CancellationToken cancellationToken)
        {
            ThrowIfDown();
            _users.TryRemove(panelUserId, out _);
            _devices.TryRemove(panelUserId, out _);
            return Task.CompletedTask;
        }

        public Task<PanelUser> RevokeSubscriptionAsync(int panelUserId, string newShortUuid, CancellationToken cancellationToken)
        {
            ThrowIfDown();
            if (!_users.TryGetValue(panelUserId, out PanelUser? user))
            {
                throw new PanelUserNotFoundException(panelUserId);
            }

            user = user with
            {
                ShortUuid = newShortUuid,
                VlessUuid = Guid.NewGuid(),
                SubscriptionUrl = $"{_subscriptionBaseUrl}/{newShortUuid}",
            };
            _users[panelUserId] = user;
            _devices.TryRemove(panelUserId, out _);
            return Task.FromResult(user);
        }

        public Task<IReadOnlyList<PanelDevice>> GetDevicesAsync(int panelUserId, CancellationToken cancellationToken)
        {
            ThrowIfDown();
            IReadOnlyList<PanelDevice> devices = _devices.TryGetValue(panelUserId, out List<PanelDevice>? list) ? list.ToList() : [];
            return Task.FromResult(devices);
        }

        public Task DeleteDeviceAsync(int panelUserId, string hwid, CancellationToken cancellationToken)
        {
            ThrowIfDown();
            if (_devices.TryGetValue(panelUserId, out List<PanelDevice>? list))
            {
                list.RemoveAll(d => d.Hwid == hwid);
            }
            return Task.CompletedTask;
        }

        public Task DeleteAllDevicesAsync(int panelUserId, CancellationToken cancellationToken)
        {
            ThrowIfDown();
            _devices.TryRemove(panelUserId, out _);
            return Task.CompletedTask;
        }

        /// <summary>Test helper: a client registers a device.</summary>
        public void AddDevice(int panelUserId, PanelDevice device) =>
            _devices.GetOrAdd(panelUserId, _ => []).Add(device);

        /// <summary>Test helper: someone edits the user in the panel UI.</summary>
        public void Tamper(int panelUserId, Func<PanelUser, PanelUser> change) =>
            _users[panelUserId] = change(_users[panelUserId]);

        private void ThrowIfDown()
        {
            if (IsDown)
            {
                throw new PanelUnavailableException("In-memory panel is down.");
            }
        }
    }
}
