using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Application.Abstractions.Probes;
using Mendeleev.Application.Panel.NodeGuard;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Common;
using Mendeleev.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mendeleev.IntegrationTests
{
    /// <summary>
    /// FR-NODE-18 (ТЗ 25, «Обнаружение блокировок и замена нод»; решение 07.10): a node blocked from Russia goes to the
    /// warm reserve only when every check agrees it is an IP block; otherwise the tech admin gets the reason. The panel
    /// is in memory, the probes are stubs: what this server and Globalping would see is set by the test.
    /// </summary>
    [Collection(nameof(PostgresCollection))]
    public sealed class NodeGuardTests(PostgresFixture postgres) : IAsyncLifetime
    {
        private const string Blocked = "198.51.100.1";
        private const string Other = "198.51.100.2";
        private const string Reserve = "198.51.100.9";

        private readonly StubLocalProbe _local = new();
        private readonly StubRemoteProbes _remote = new();
        private TestApp _app = null!;

        public Task InitializeAsync() => StartAsync(autoReplace: true);

        public async Task DisposeAsync() => await _app.DisposeAsync();

        [Fact]
        public async Task ABlockedNode_GoesToTheReserve_AfterTwoChecks_ReserveFirst()
        {
            Nodes();
            _local.Down(Blocked);

            NodeCheckSummary first = await CheckAsync();
            first.Checked.ShouldBe(2);
            first.Down.ShouldBe(["NL1"]);
            first.Replaced.ShouldBeNull();
            _app.Alerts.Raised.ShouldBeEmpty();
            HostsOf("RES1").ShouldAllBe(h => h.IsDisabled);

            NodeCheckSummary second = await CheckAsync();
            second.Replaced.ShouldBe("NL1");
            HostsOf("NL1").ShouldAllBe(h => h.IsDisabled);
            HostsOf("RES1").ShouldAllBe(h => !h.IsDisabled);
            HostsOf("DE1").ShouldAllBe(h => !h.IsDisabled);
            _remote.Calls.ShouldBe(1);

            Alert alert = _app.Alerts.Raised.ShouldHaveSingleItem();
            alert.Key.ShouldBe("node-guard:NL1:replaced");
            alert.Severity.ShouldBe(AlertSeverity.Critical);
            alert.Audience.ShouldBe(AlertAudience.TechAdminAndAdmin);
            alert.Text.ShouldContain("3 из 3 проб Globalping");

            AuditLogEntry audit = await _app.WithDbAsync(db => db.AuditLog.AsNoTracking().SingleAsync(a => a.Action == AuditActions.NodeAutoReplace));
            audit.StaffId.ShouldBeNull();
            using (var details = System.Text.Json.JsonDocument.Parse(audit.Details!))
            {
                details.RootElement.GetProperty("blocked").GetString().ShouldBe("NL1");
                details.RootElement.GetProperty("reserve").GetString().ShouldBe("RES1");
            }
            (await LastReplacementAsync()).ShouldNotBeNull().Value.ShouldEndWith("NL1 → RES1");

            // The replaced node left the active set: nothing more about it. The reserve now serves users and is watched.
            NodeCheckSummary third = await CheckAsync();
            third.Checked.ShouldBe(2);
            third.Down.ShouldBeEmpty();
            third.Replaced.ShouldBeNull();
            _app.Alerts.Raised.Count.ShouldBe(1);

            // Blocked in its turn, the reserve has no reserve left.
            _local.Down(Reserve);
            _app.Clock.Advance(TimeSpan.FromHours(7));
            await CheckTwiceAsync();
            _app.Alerts.Raised.Last().Key.ShouldBe("node-guard:RES1:reserve-busy");
            HostsOf("RES1").ShouldAllBe(h => !h.IsDisabled);
        }

        [Fact]
        public async Task OneFailedCheck_ThenAnAnswer_ChangesNothing()
        {
            Nodes();
            _local.Down(Blocked);
            await CheckAsync();
            _local.Up(Blocked);
            await CheckAsync();
            _local.Down(Blocked);

            (await CheckAsync()).Replaced.ShouldBeNull();
            _remote.Calls.ShouldBe(0);
            _app.Alerts.Raised.ShouldBeEmpty();
        }

        [Fact]
        public async Task GlobalpingReachingTheNode_IsOurRoute_NotABlock()
        {
            Nodes();
            _local.Down(Blocked);
            _remote.Result = new RemoteProbeResult(3, 2);

            await CheckTwiceAsync();

            OnlyAlert().ShouldSatisfyAllConditions(
                a => a.Key.ShouldBe("node-guard:NL1:route"),
                a => a.Severity.ShouldBe(AlertSeverity.Warning));
            HostsOf("RES1").ShouldAllBe(h => h.IsDisabled);
        }

        [Fact]
        public async Task NoAnswerFromGlobalping_IsReportedForAManualReplacement()
        {
            Nodes();
            _local.Down(Blocked);
            _remote.Result = null;

            await CheckTwiceAsync();

            Alert alert = OnlyAlert();
            alert.Key.ShouldBe("node-guard:NL1:unconfirmed");
            alert.Text.ShouldContain("replace-node.sh NL1 RES1");
            HostsOf("NL1").ShouldAllBe(h => !h.IsDisabled);
        }

        [Fact]
        public async Task ANodeThePanelLostToo_IsAFailure_NotABlock()
        {
            Nodes();
            _app.Panel.SetNodeConnected("NL1", false);
            _local.Down(Blocked);

            await CheckTwiceAsync();

            OnlyAlert().Key.ShouldBe("node-guard:NL1:dead");
            HostsOf("RES1").ShouldAllBe(h => h.IsDisabled);
        }

        [Fact]
        public async Task EveryNodeDown_IsNotOneBlock_AndGoesToPeople()
        {
            Nodes();
            _local.Down(Blocked);
            _local.Down(Other);

            await CheckTwiceAsync();

            OnlyAlert().Key.ShouldBe("node-guard:all:down");
            _remote.Calls.ShouldBe(0);
            HostsOf("RES1").ShouldAllBe(h => h.IsDisabled);
        }

        [Fact]
        public async Task ControlSitesDown_TheChecksDoNotCount()
        {
            Nodes();
            _local.Down(Blocked);
            _local.Down("ya.ru");
            _local.Down("vk.com");

            (await CheckAsync()).ShouldBe(NodeCheckSummary.Skipped);
            (await CheckAsync()).ShouldBe(NodeCheckSummary.Skipped);

            _local.Up("ya.ru");
            (await CheckAsync()).Replaced.ShouldBeNull();
            _app.Alerts.Raised.ShouldBeEmpty();
        }

        [Fact]
        public async Task ASecondBlock_WithinTheCooldown_IsForPeople_AndAfterIt_TheReserveIsBusy()
        {
            Nodes();
            _local.Down(Blocked);
            await CheckTwiceAsync();
            _app.Alerts.Raised.Clear();

            _local.Down(Other);
            await CheckTwiceAsync();
            OnlyAlert().Key.ShouldBe("node-guard:DE1:cooldown");
            HostsOf("DE1").ShouldAllBe(h => !h.IsDisabled);

            // Seven hours on, DE1 answers for a while and is blocked again: the cooldown is over, the reserve is taken.
            _app.Clock.Advance(TimeSpan.FromHours(7));
            _local.Up(Other);
            await CheckAsync();
            _app.Alerts.Raised.ShouldContain(a => a.Key == "node-guard:DE1:back" && a.Severity == AlertSeverity.Info);
            _app.Alerts.Raised.Clear();

            _local.Down(Other);
            await CheckTwiceAsync();
            OnlyAlert().Key.ShouldBe("node-guard:DE1:reserve-busy");
        }

        [Fact]
        public async Task AReserveThePanelCannotReach_IsNotSwitchedTo()
        {
            Nodes(reserveConnected: false);
            _local.Down(Blocked);

            await CheckTwiceAsync();

            OnlyAlert().Key.ShouldBe("node-guard:NL1:reserve-offline");
            HostsOf("NL1").ShouldAllBe(h => !h.IsDisabled);
        }

        [Fact]
        public async Task AReserveBlockedFromHereToo_IsNotSwitchedTo()
        {
            Nodes();
            _local.Down(Blocked);
            _local.Down(Reserve);

            await CheckTwiceAsync();

            OnlyAlert().Key.ShouldBe("node-guard:NL1:reserve-blocked");
            HostsOf("RES1").ShouldAllBe(h => h.IsDisabled);
        }

        [Fact]
        public async Task WithoutAReserve_TheBlockIsReported()
        {
            _app.Panel.AddNode("NL1", connected: true, hostsDisabled: false, (Blocked, 443));
            _app.Panel.AddNode("DE1", connected: true, hostsDisabled: false, (Other, 443));
            _local.Down(Blocked);

            await CheckTwiceAsync();

            OnlyAlert().Key.ShouldBe("node-guard:NL1:no-reserve");
        }

        [Fact]
        public async Task WithTheSwitchOff_AConfirmedBlockIsOnlyReported()
        {
            await _app.DisposeAsync();
            await StartAsync(autoReplace: false);
            Nodes();
            _local.Down(Blocked);

            await CheckTwiceAsync();

            Alert alert = OnlyAlert();
            alert.Key.ShouldBe("node-guard:NL1:blocked");
            alert.Severity.ShouldBe(AlertSeverity.Critical);
            alert.Text.ShouldContain("NodeGuard__AutoReplace");
            HostsOf("NL1").ShouldAllBe(h => !h.IsDisabled);
            HostsOf("RES1").ShouldAllBe(h => h.IsDisabled);
            (await LastReplacementAsync()).ShouldBeNull();
        }

        [Fact]
        public async Task Disabled_ProbesNothing()
        {
            await _app.DisposeAsync();
            _app = await TestApp.CreateAsync(postgres, configureServices: Stubs);
            Nodes();
            _local.Down(Blocked);

            (await CheckAsync()).ShouldBe(NodeCheckSummary.Skipped);
            (await CheckAsync()).ShouldBe(NodeCheckSummary.Skipped);

            _local.Calls.ShouldBe(0);
            HostsOf("NL1").ShouldAllBe(h => !h.IsDisabled);
        }

        [Fact]
        public async Task AlertmanagerWebhook_ReachesTheAlertBot_OnlyFromInside()
        {
            var alerts = new RecordingAlerts();
            await using CabinetSite site = await CabinetSite.CreateAsync(postgres);
            using WebApplicationFactory<Web.Program> host = site.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services => services.AddSingleton<IAlertSink>(alerts)));
            using HttpClient client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            const string body = """
                {"version":"4","status":"firing","alerts":[{"status":"firing","labels":{"alertname":"DiskSpaceLow","severity":"warning"},"annotations":{"summary":"Мало места"},"fingerprint":"f1"}]}
                """;

            using (HttpResponseMessage inside = await client.PostAsync("/internal/alertmanager", new StringContent(body, Encoding.UTF8, "application/json")))
            {
                inside.StatusCode.ShouldBe(HttpStatusCode.OK);
            }
            alerts.Raised.ShouldHaveSingleItem().ShouldBe(new Alert(AlertSeverity.Warning, "prometheus:DiskSpaceLow:f1:firing", "Мало места"));

            // Through Caddy a request carries X-Forwarded-For: refused as /metrics is.
            using var proxied = new HttpRequestMessage(HttpMethod.Post, "/internal/alertmanager")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            proxied.Headers.Add("X-Forwarded-For", "203.0.113.5");
            using (HttpResponseMessage outside = await client.SendAsync(proxied))
            {
                outside.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }
            alerts.Raised.Count.ShouldBe(1);
        }

        private async Task StartAsync(bool autoReplace) =>
            _app = await TestApp.CreateAsync(
                postgres,
                new Dictionary<string, string?>
                {
                    ["NodeGuard:Enabled"] = "true",
                    ["NodeGuard:AutoReplace"] = autoReplace ? "true" : "false",
                },
                Stubs);

        private void Stubs(IServiceCollection services)
        {
            services.AddSingleton<ILocalProbe>(_local);
            services.AddSingleton<IRemoteProbes>(_remote);
        }

        /// <summary>NL1 with two transports on one address, DE1, and the warm reserve RES1 with its hosts disabled.</summary>
        private void Nodes(bool reserveConnected = true)
        {
            _app.Panel.AddNode("NL1", connected: true, hostsDisabled: false, (Blocked, 443), (Blocked, 8443));
            _app.Panel.AddNode("DE1", connected: true, hostsDisabled: false, (Other, 443));
            _app.Panel.AddNode("RES1", connected: reserveConnected, hostsDisabled: true, (Reserve, 443), (Reserve, 8443));
        }

        private IReadOnlyList<PanelHost> HostsOf(string node) => _app.Panel.Hosts.Where(h => h.NodeCode == node).ToList();

        private Alert OnlyAlert() => _app.Alerts.Raised.ShouldHaveSingleItem();

        private async Task<NodeCheckSummary> CheckAsync() =>
            (await _app.SendAsync<CheckNodesCommand, NodeCheckSummary>(new CheckNodesCommand())).Value;

        private async Task CheckTwiceAsync()
        {
            await CheckAsync();
            await CheckAsync();
        }

        private Task<ServiceSetting?> LastReplacementAsync() =>
            _app.WithDbAsync(db => db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == ServiceSetting.NodeGuardLastReplacement));

        private sealed class StubLocalProbe : ILocalProbe
        {
            private readonly ConcurrentDictionary<string, bool> _down = new();
            private int _calls;

            public int Calls => _calls;

            public void Down(string host) => _down[host] = true;

            public void Up(string host) => _down.TryRemove(host, out _);

            public Task<bool> CanConnectAsync(string host, int port, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _calls);
                return Task.FromResult(!_down.ContainsKey(host));
            }
        }

        private sealed class StubRemoteProbes : IRemoteProbes
        {
            private int _calls;

            /// <summary>What Globalping answers; null — it does not.</summary>
            public RemoteProbeResult? Result { get; set; } = new(3, 0);

            public int Calls => _calls;

            public Task<RemoteProbeResult> TcpAsync(string host, int port, string country, int probes, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _calls);
                return Result is { } result
                    ? Task.FromResult(result)
                    : throw new RemoteProbesUnavailableException("Globalping is down in this test.");
            }
        }
    }
}
