using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mendeleev.Application.Abstractions.Panel;

namespace Mendeleev.ContractTests
{
    /// <summary>
    /// The node guard's <see cref="IPanelInventory"/> against the pinned panel (FR-NODE-18), through the token with only
    /// <see cref="RemnawavePanel.InventoryTokenScopes"/>: the calls replace-node.sh makes. The nodes are arranged as the
    /// operator does it (staging-panel-setup.py) and never connect: there is no node behind them.
    /// </summary>
    [Collection(nameof(RemnawavePanelCollection))]
    public sealed class RemnawaveInventoryContractTests(RemnawavePanel panel)
    {
        [Fact]
        public async Task NodesAndHosts_AreRead_AndTheHostsSwitched_ReserveFirst()
        {
            string suffix = Random.Shared.Next(100, 1000).ToString(CultureInfo.InvariantCulture);
            string node = "NG" + suffix;
            string reserve = "RS" + suffix;
            (Guid profile, Dictionary<string, Guid> inbounds) = await CreateProfileAsync("contract-ng-" + suffix);
            Guid nodeUuid = await CreateNodeAsync(node, "192.0.2.10", profile, inbounds);
            Guid reserveUuid = await CreateNodeAsync(reserve, "192.0.2.20", profile, inbounds);
            Guid[] nodeHosts =
            [
                await CreateHostAsync(node, "192.0.2.10", 443, "VLESS_REALITY", profile, inbounds, disabled: false),
                await CreateHostAsync(node, "192.0.2.10", 8443, "VLESS_XHTTP", profile, inbounds, disabled: false),
            ];
            Guid[] reserveHosts =
            [
                await CreateHostAsync(reserve, "192.0.2.20", 443, "VLESS_REALITY", profile, inbounds, disabled: true),
                await CreateHostAsync(reserve, "192.0.2.20", 8443, "VLESS_XHTTP", profile, inbounds, disabled: true),
            ];

            IReadOnlyList<PanelNode> nodes = await panel.Inventory.GetNodesAsync(CancellationToken.None);
            nodes.ShouldContain(n => n.Uuid == nodeUuid && n.Name == node && !n.IsConnected && !n.IsDisabled);
            nodes.ShouldContain(n => n.Uuid == reserveUuid && n.Name == reserve);

            IReadOnlyList<PanelHost> hosts = await panel.Inventory.GetHostsAsync(CancellationToken.None);
            PanelHost reality = hosts.Single(h => h.Uuid == nodeHosts[0]);
            reality.Remark.ShouldBe($"{node} 443");
            reality.Address.ShouldBe("192.0.2.10");
            reality.Port.ShouldBe(443);
            reality.NodeCode.ShouldBe(node);
            reality.IsDisabled.ShouldBeFalse();
            hosts.Single(h => h.Uuid == nodeHosts[1]).Port.ShouldBe(8443);
            hosts.Where(h => reserveHosts.Contains(h.Uuid)).ShouldAllBe(h => h.IsDisabled && h.NodeCode == reserve);

            await panel.Inventory.SetHostsDisabledAsync(reserveHosts, disabled: false, CancellationToken.None);
            await panel.Inventory.SetHostsDisabledAsync(nodeHosts, disabled: true, CancellationToken.None);

            hosts = await panel.Inventory.GetHostsAsync(CancellationToken.None);
            hosts.Where(h => nodeHosts.Contains(h.Uuid)).ShouldAllBe(h => h.IsDisabled);
            hosts.Where(h => reserveHosts.Contains(h.Uuid)).ShouldAllBe(h => !h.IsDisabled);

            // The same call again ends the same: the resilience pipeline may retry a bulk call.
            await panel.Inventory.SetHostsDisabledAsync(nodeHosts, disabled: true, CancellationToken.None);
            (await panel.Inventory.GetHostsAsync(CancellationToken.None)).Where(h => nodeHosts.Contains(h.Uuid)).ShouldAllBe(h => h.IsDisabled);
        }

        [Fact]
        public async Task TheNodeGuardsToken_ReachesHosts_ButNotUsers()
        {
            using var client = new HttpClient(new ReverseProxyHeaders { InnerHandler = new HttpClientHandler() })
            {
                BaseAddress = new Uri(panel.BaseUrl + "/"),
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", panel.InventoryToken);

            using HttpResponseMessage hosts = await client.GetAsync("api/hosts");
            hosts.StatusCode.ShouldBe(HttpStatusCode.OK);
            using HttpResponseMessage users = await client.GetAsync("api/users?size=1&start=0");
            users.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        private async Task<(Guid Profile, Dictionary<string, Guid> Inbounds)> CreateProfileAsync(string name)
        {
            // The template of the real profile; the panel generates the Reality key pair, as in step profile.
            JsonElement keys = await panel.AdminAsync(HttpMethod.Get, "api/system/tools/x25519/generate");
            string privateKey = keys.GetProperty("keypairs")[0].GetProperty("privateKey").GetString()!;
            JsonNode config = JsonNode.Parse(File.ReadAllText(Template()).Replace("REALITY_PRIVATE_KEY", privateKey, StringComparison.Ordinal))!;

            JsonElement profile = await panel.AdminAsync(HttpMethod.Post, "api/config-profiles", new { name, config });
            Guid uuid = profile.GetProperty("uuid").GetGuid();
            JsonElement inbounds = await panel.AdminAsync(HttpMethod.Get, $"api/config-profiles/{uuid}/inbounds");
            return (uuid, inbounds.GetProperty("inbounds").EnumerateArray()
                .ToDictionary(i => i.GetProperty("tag").GetString()!, i => i.GetProperty("uuid").GetGuid()));
        }

        private async Task<Guid> CreateNodeAsync(string name, string address, Guid profile, Dictionary<string, Guid> inbounds)
        {
            JsonElement node = await panel.AdminAsync(HttpMethod.Post, "api/nodes", new
            {
                name,
                address,
                port = 2222,
                countryCode = "NL",
                configProfile = new { activeConfigProfileUuid = profile, activeInbounds = inbounds.Values },
            });
            return node.GetProperty("uuid").GetGuid();
        }

        private async Task<Guid> CreateHostAsync(
            string node, string address, int port, string inboundTag, Guid profile, Dictionary<string, Guid> inbounds, bool disabled)
        {
            JsonElement host = await panel.AdminAsync(HttpMethod.Post, "api/hosts", new
            {
                inbound = new { configProfileUuid = profile, configProfileInboundUuid = inbounds[inboundTag] },
                remark = $"{node} {port}",
                address,
                port,
                tags = new[] { node },
                isDisabled = disabled,
            });
            Guid uuid = host.GetProperty("uuid").GetGuid();

            // As staging-panel-setup.py: a create may leave the host enabled.
            if (disabled && !host.GetProperty("isDisabled").GetBoolean())
            {
                await panel.AdminAsync(HttpMethod.Post, "api/hosts/bulk/disable", new { uuids = new[] { uuid } });
            }
            return uuid;
        }

        private static string Template()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                string path = Path.Combine(directory.FullName, "infra", "xray", "config-profile.example.json");
                if (File.Exists(path))
                {
                    return path;
                }
            }

            throw new InvalidOperationException("infra/xray/config-profile.example.json not found above the test directory.");
        }
    }
}
