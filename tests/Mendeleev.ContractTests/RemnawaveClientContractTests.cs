using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Domain.Subscriptions;
using Mendeleev.Domain.Users;

namespace Mendeleev.ContractTests
{
    /// <summary>
    /// Every call of <see cref="IPanelClient"/> against the pinned panel (FR-PNL-01, ТЗ 24 «API и контракты»),
    /// through the token with only the service's scopes. Each test works with its own users.
    /// </summary>
    [Collection(nameof(RemnawavePanelCollection))]
    public sealed class RemnawaveClientContractTests(RemnawavePanel panel)
    {
        private static long _nextUserId = Random.Shared.Next(1, 1_000) * 1_000L;

        private IPanelClient Client => panel.Client;

        [Fact]
        public async Task Create_KeepsOurIdentifiers_AndReturnsTheDesiredState()
        {
            // ТЗ 40: shortUuid and VLESS UUID are ours, so the panel can be rebuilt from our database.
            PanelUserSpec spec = Spec();

            PanelUser user = await Client.CreateUserAsync(spec, CancellationToken.None);

            user.Id.ShouldBeGreaterThan(0);
            user.Username.ShouldBe(spec.Username);
            user.ShortUuid.ShouldBe(spec.ShortUuid);
            user.VlessUuid.ShouldBe(spec.VlessUuid);
            user.Status.ShouldBe(PanelUserStatus.Active);
            user.ExpireAt.ShouldBe(spec.ExpireAt, TimeSpan.FromSeconds(1));
            user.TrafficLimitBytes.ShouldBe(spec.TrafficLimitBytes);
            user.HwidDeviceLimit.ShouldBe(spec.HwidDeviceLimit);
            user.InternalSquads.ShouldBe([panel.BasicSquad]);
            user.SubscriptionUrl.ShouldContain(RemnawavePanel.SubscriptionDomain);
            user.SubscriptionUrl.ShouldEndWith(spec.ShortUuid);
            user.UsedTrafficBytes.ShouldBe(0);
            user.FirstConnectedAt.ShouldBeNull();
        }

        [Fact]
        public async Task Create_SmallUserIds_AreValidUsernames()
        {
            // The first users of the service have ids 1, 2, …: their pseudonyms must pass the panel's rules.
            foreach (long userId in new long[] { 1, 9, 10 })
            {
                PanelUserSpec spec = Spec() with { Username = User.PanelUsernameFor(userId) };
                if (await Client.GetUserByUsernameAsync(spec.Username, CancellationToken.None) is PanelUser existing)
                {
                    await Client.DeleteUserAsync(existing.Id, CancellationToken.None);
                }

                PanelUser user = await Client.CreateUserAsync(spec, CancellationToken.None);

                User.TryParsePanelUsername(user.Username, out long parsed).ShouldBeTrue();
                parsed.ShouldBe(userId);
                await Client.DeleteUserAsync(user.Id, CancellationToken.None);
            }
        }

        [Fact]
        public async Task Create_SameUsername_IsAConflict()
        {
            // The outbox adopts the existing user on conflict (ТЗ 24, «Обработка ошибок»).
            PanelUserSpec spec = Spec();
            await Client.CreateUserAsync(spec, CancellationToken.None);

            await Should.ThrowAsync<PanelConflictException>(() =>
                Client.CreateUserAsync(spec with { ShortUuid = PanelIdentifiers.NewShortUuid(), VlessUuid = Guid.NewGuid() }, CancellationToken.None));
        }

        [Fact]
        public async Task Update_PushesTheFullDesiredState()
        {
            PanelUser created = await Client.CreateUserAsync(Spec(), CancellationToken.None);
            PanelUserSpec changed = Spec(created.Username) with
            {
                ShortUuid = created.ShortUuid,
                VlessUuid = created.VlessUuid,
                ExpireAt = Now().AddDays(90),
                TrafficLimitBytes = 0,
                HwidDeviceLimit = 5,
                Enabled = false,
            };

            PanelUser disabled = await Client.UpdateUserAsync(created.Id, changed, CancellationToken.None);

            disabled.Status.ShouldBe(PanelUserStatus.Disabled);
            disabled.ExpireAt.ShouldBe(changed.ExpireAt, TimeSpan.FromSeconds(1));
            disabled.TrafficLimitBytes.ShouldBe(0);
            disabled.HwidDeviceLimit.ShouldBe(5);

            PanelUser enabled = await Client.UpdateUserAsync(created.Id, changed with { Enabled = true }, CancellationToken.None);
            enabled.Status.ShouldBe(PanelUserStatus.Active);

            (await Client.GetUserAsync(created.Id, CancellationToken.None))!.HwidDeviceLimit.ShouldBe(5);
        }

        [Fact]
        public async Task Update_WithAPastExpiry_KeepsThePanelDate()
        {
            // The panel refuses a past expireAt; the adapter leaves it out and the panel expires the user itself.
            PanelUser created = await Client.CreateUserAsync(Spec(), CancellationToken.None);

            PanelUser updated = await Client.UpdateUserAsync(
                created.Id,
                Spec(created.Username) with { ShortUuid = created.ShortUuid, VlessUuid = created.VlessUuid, ExpireAt = Now().AddDays(-1) },
                CancellationToken.None);

            updated.ExpireAt.ShouldBe(created.ExpireAt, TimeSpan.FromSeconds(1));
        }

        [Fact]
        public async Task Get_ByIdAndByUsername_AndUnknownIsNull()
        {
            PanelUser created = await Client.CreateUserAsync(Spec(), CancellationToken.None);

            (await Client.GetUserAsync(created.Id, CancellationToken.None))!.Username.ShouldBe(created.Username);
            (await Client.GetUserByUsernameAsync(created.Username, CancellationToken.None))!.Id.ShouldBe(created.Id);

            (await Client.GetUserAsync(int.MaxValue - 7, CancellationToken.None)).ShouldBeNull();
            (await Client.GetUserByUsernameAsync("u999999999999", CancellationToken.None)).ShouldBeNull();
        }

        [Fact]
        public async Task List_WalksAllPages()
        {
            // Reconciliation reads the whole panel page by page (FR-PNL-05).
            List<PanelUser> created = [];
            for (int i = 0; i < 5; i++)
            {
                created.Add(await Client.CreateUserAsync(Spec(), CancellationToken.None));
            }

            List<PanelUser> listed = [];
            await foreach (PanelUser user in panel.ClientWithPageSize(2).ListUsersAsync(CancellationToken.None))
            {
                listed.Add(user);
            }

            listed.Select(u => u.Id).ShouldBeUnique();
            created.Select(u => u.Id).ShouldBeSubsetOf(listed.Select(u => u.Id));
        }

        [Fact]
        public async Task Revoke_SetsOurNewShortUuid_AndTheLinkChanges()
        {
            // FR-PNL-11: the old link stops working; the new short UUID is ours.
            PanelUser created = await Client.CreateUserAsync(Spec(), CancellationToken.None);
            string newShortUuid = PanelIdentifiers.NewShortUuid();

            PanelUser revoked = await Client.RevokeSubscriptionAsync(created.Id, newShortUuid, CancellationToken.None);

            revoked.ShortUuid.ShouldBe(newShortUuid);
            revoked.SubscriptionUrl.ShouldEndWith(newShortUuid);
            revoked.SubscriptionUrl.ShouldNotBe(created.SubscriptionUrl);
            revoked.VlessUuid.ShouldNotBe(created.VlessUuid);
        }

        [Fact]
        public async Task Devices_List_DeleteOne_DeleteAll()
        {
            // FR-PNL-09, FR-PNL-10.
            PanelUser user = await Client.CreateUserAsync(Spec(), CancellationToken.None);
            (await Client.GetDevicesAsync(user.Id, CancellationToken.None)).ShouldBeEmpty();

            await panel.AddDeviceAsync(user.Id, "hwid-contract-0001", "android");
            await panel.AddDeviceAsync(user.Id, "hwid-contract-0002", "ios");

            IReadOnlyList<PanelDevice> devices = await Client.GetDevicesAsync(user.Id, CancellationToken.None);
            devices.Select(d => d.Hwid).OrderBy(h => h).ShouldBe(["hwid-contract-0001", "hwid-contract-0002"]);
            devices.Single(d => d.Hwid == "hwid-contract-0001").Platform.ShouldBe("android");
            devices.ShouldAllBe(d => d.DeviceModel == "Contract Phone" && d.CreatedAt > DateTime.UtcNow.AddHours(-1));

            await Client.DeleteDeviceAsync(user.Id, "hwid-contract-0001", CancellationToken.None);
            (await Client.GetDevicesAsync(user.Id, CancellationToken.None)).Select(d => d.Hwid).ShouldBe(["hwid-contract-0002"]);

            await Client.DeleteAllDevicesAsync(user.Id, CancellationToken.None);
            (await Client.GetDevicesAsync(user.Id, CancellationToken.None)).ShouldBeEmpty();
        }

        [Fact]
        public async Task Delete_IsIdempotent_AndUpdatingAGoneUserIsNotFound()
        {
            PanelUser user = await Client.CreateUserAsync(Spec(), CancellationToken.None);

            await Client.DeleteUserAsync(user.Id, CancellationToken.None);
            await Client.DeleteUserAsync(user.Id, CancellationToken.None);

            (await Client.GetUserAsync(user.Id, CancellationToken.None)).ShouldBeNull();
            await Should.ThrowAsync<PanelUserNotFoundException>(() =>
                Client.UpdateUserAsync(user.Id, Spec(user.Username), CancellationToken.None));
        }

        [Fact]
        public async Task InvalidRequest_IsAContractError_NotARetry()
        {
            // A 4xx means a bug or an incompatible panel: the outbox must not retry it forever.
            PanelUserSpec spec = Spec() with { Username = "not a pseudonym" };

            PanelException error = await Should.ThrowAsync<PanelException>(() => Client.CreateUserAsync(spec, CancellationToken.None));

            error.ShouldBeOfType<PanelContractException>();
        }

        [Fact]
        public async Task Ping_AnswersWithinTheServiceTokenScopes()
        {
            // The availability monitor asks once a minute (ТЗ 24, «Панель недоступна дольше 5 минут»).
            await Should.NotThrowAsync(() => Client.PingAsync(CancellationToken.None));
        }

        [Fact]
        public async Task Webhook_IsSignedAsTheParserExpects_AndCarriesTheUser()
        {
            // FR-PNL-13: the panel reports changes; the service checks the signature and the age.
            PanelUser created = await Client.CreateUserAsync(Spec(), CancellationToken.None);

            WebhookRequest request = await panel.WaitForWebhookAsync(
                e => e.Scope == "user" && e.User?.Id == created.Id,
                TimeSpan.FromSeconds(30));

            PanelWebhookEvent webhook = panel.WebhookParser.Parse(request);
            webhook.Event.ShouldStartWith("user.");
            webhook.User!.Username.ShouldBe(created.Username);
            webhook.User.ShortUuid.ShouldBe(created.ShortUuid);
            webhook.Timestamp.ShouldBe(DateTime.UtcNow, TimeSpan.FromMinutes(1));

            // A body changed on the way does not pass.
            var tampered = request with { Body = [.. request.Body, (byte)' '] };
            Should.Throw<WebhookAuthenticationException>(() => panel.WebhookParser.Parse(tampered));
        }

        private PanelUserSpec Spec(string? username = null) => new(
            username ?? User.PanelUsernameFor(Interlocked.Increment(ref _nextUserId)),
            PanelIdentifiers.NewShortUuid(),
            Guid.NewGuid(),
            Now().AddDays(30),
            TrafficLimitBytes: 10L * 1024 * 1024 * 1024,
            HwidDeviceLimit: 3,
            InternalSquads: [panel.BasicSquad],
            Enabled: true);

        /// <summary>Whole seconds: the panel keeps milliseconds, comparisons stay exact.</summary>
        private static DateTime Now() => DateTime.UtcNow.AddTicks(-(DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond));
    }
}
