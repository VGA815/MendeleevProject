using System.Collections.Concurrent;
using System.Globalization;
using Mendeleev.Application.Abstractions;
using Mendeleev.Application.Abstractions.Alerts;
using Mendeleev.Application.Abstractions.Data;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Observability;
using Mendeleev.Application.Abstractions.Panel;
using Mendeleev.Application.Abstractions.Probes;
using Mendeleev.Application.Configuration;
using Mendeleev.Domain.Audit;
using Mendeleev.Domain.Common;
using Mendeleev.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mendeleev.Application.Panel.NodeGuard
{
    /// <summary>
    /// Every 5 minutes: the automatic replacement of a node blocked from Russia (FR-NODE-18; ТЗ 25, «Обнаружение
    /// блокировок и замена нод»; решение 07.10). The hosts of each active node are probed by TCP from this server, which
    /// sits in a Russian data center. A node that fails two checks in a row is switched to the warm reserve only when
    /// everything else agrees it is an IP block: our control sites open, most Globalping probes in Russia cannot reach
    /// it either, and the panel abroad still sees it alive. Otherwise — and when the switch is off, the reserve is busy,
    /// unreachable or a replacement already happened within the cooldown — the tech admin gets the reason instead.
    /// </summary>
    public sealed record CheckNodesCommand : ICommand<NodeCheckSummary>;

    /// <param name="Down">Nodes with no host reachable from here in this check.</param>
    /// <param name="Replaced">The node switched to the reserve by this check.</param>
    public sealed record NodeCheckSummary(int Checked, IReadOnlyList<string> Down, string? Replaced)
    {
        public static readonly NodeCheckSummary Skipped = new(0, [], null);
    }

    /// <summary>
    /// Failed checks in a row per node, between runs. In memory on purpose: after a restart the count starts again, which
    /// only delays a replacement by one check.
    /// </summary>
    internal sealed class NodeGuardState
    {
        private readonly ConcurrentDictionary<string, Watch> _nodes = new(StringComparer.OrdinalIgnoreCase);

        public int Fail(string node) => _nodes.AddOrUpdate(node, _ => new Watch(1, false), (_, w) => w with { Failures = w.Failures + 1 }).Failures;

        public int Failures(string node) => _nodes.TryGetValue(node, out Watch? w) ? w.Failures : 0;

        public bool IsHandled(string node) => _nodes.TryGetValue(node, out Watch? w) && w.Handled;

        public void MarkHandled(string node) => _nodes.AddOrUpdate(node, _ => new Watch(0, true), (_, w) => w with { Handled = true });

        /// <returns>True if the node was reported while down: its recovery is news.</returns>
        public bool Recover(string node) => _nodes.TryRemove(node, out Watch? w) && w.Handled;

        /// <summary>Nodes that left the active set (replaced, removed) are no longer watched.</summary>
        public void Keep(IReadOnlyCollection<string> active)
        {
            foreach (string node in _nodes.Keys.Where(n => !active.Contains(n, StringComparer.OrdinalIgnoreCase)))
            {
                _nodes.TryRemove(node, out _);
            }
        }

        private sealed record Watch(int Failures, bool Handled);
    }

    internal sealed class CheckNodesCommandHandler(
        IPanelInventory inventory,
        ILocalProbe localProbe,
        IRemoteProbes remoteProbes,
        NodeGuardState state,
        IApplicationDbContext db,
        IAlertSink alerts,
        IDateTimeProvider clock,
        IOptions<NodeGuardOptions> options,
        ILogger<CheckNodesCommandHandler> logger)
        : ICommandHandler<CheckNodesCommand, NodeCheckSummary>
    {
        public async Task<Result<NodeCheckSummary>> Handle(CheckNodesCommand command, CancellationToken cancellationToken)
        {
            NodeGuardOptions settings = options.Value;
            if (!settings.Enabled || !inventory.IsConfigured)
            {
                return NodeCheckSummary.Skipped;
            }

            IReadOnlyList<PanelHost> hosts;
            try
            {
                hosts = await inventory.GetHostsAsync(cancellationToken);
            }
            catch (PanelException ex)
            {
                // The panel availability monitor reports an unreachable panel; without hosts there is nothing to probe.
                logger.LogWarning(ex, "Node guard: no hosts from the panel");
                return NodeCheckSummary.Skipped;
            }

            // Our own network first: if the control sites do not open either, nothing seen here is about the nodes.
            if (settings.ControlTargets.Count > 0 && !await AnyConnectsAsync(settings.ControlTargets.Select(ParseTarget), cancellationToken))
            {
                logger.LogWarning("Node guard: control targets are unreachable from here, the check does not count");
                return NodeCheckSummary.Skipped;
            }

            // Every node whose hosts are in the subscriptions — the reserve too, once it stands in for a blocked node.
            List<IGrouping<string, PanelHost>> active = hosts
                .Where(h => !h.IsDisabled && h.NodeCode is not null)
                .GroupBy(h => h.NodeCode!, StringComparer.OrdinalIgnoreCase)
                .ToList();
            state.Keep(active.Select(g => g.Key).ToList());

            // Every transport (host) is probed, all at once; a node is up while any of its transports is.
            Dictionary<(string Host, int Port), bool> reachable = await ProbeAsync(active.SelectMany(n => n).Select(h => (h.Address, h.Port)), cancellationToken);
            var down = new List<string>();
            var nodeProbes = new List<(string Node, bool Up)>();
            var transportProbes = new List<(string Node, string Transport, bool Up)>();
            foreach (IGrouping<string, PanelHost> node in active)
            {
                bool up = false;
                foreach (PanelHost host in node)
                {
                    bool reached = reachable[(host.Address, host.Port)];
                    transportProbes.Add((node.Key, host.Remark, reached));
                    up |= reached;
                }
                nodeProbes.Add((node.Key, up));

                if (up)
                {
                    if (state.Recover(node.Key))
                    {
                        await AlertAsync(AlertSeverity.Info, node.Key, "back", $"Нода {node.Key} снова доступна из нашего ДЦ.", cancellationToken);
                    }
                    continue;
                }

                down.Add(node.Key);
                state.Fail(node.Key);
            }
            AppMetrics.SetProbes(nodeProbes, transportProbes);

            List<IGrouping<string, PanelHost>> due = active
                .Where(n => state.Failures(n.Key) >= settings.FailuresInARow && !state.IsHandled(n.Key))
                .ToList();
            if (due.Count == 0)
            {
                return new NodeCheckSummary(active.Count, down, null);
            }

            if (active.Count > 1 && down.Count == active.Count)
            {
                // All at once while the control sites open: our route abroad or a wholesale block — one reserve
                // cannot stand in for every node, so people decide.
                foreach (IGrouping<string, PanelHost> node in due)
                {
                    state.MarkHandled(node.Key);
                }
                await AlertAsync(
                    AlertSeverity.Critical,
                    "all",
                    "down",
                    $"Все ноды ({string.Join(", ", down)}) недоступны из нашего ДЦ, хотя контрольные сайты открываются. Автозамена не выполняется: один резерв не заменит все ноды. Проверьте маршрут управляющего VPS за рубеж и пробы (runbook node-blocked).",
                    cancellationToken);
                return new NodeCheckSummary(active.Count, down, null);
            }

            string? replaced = null;
            foreach (IGrouping<string, PanelHost> node in due)
            {
                state.MarkHandled(node.Key);
                if (await ReplaceOrReportAsync(node.Key, node.ToList(), hosts, reserveTaken: replaced is not null, settings, cancellationToken))
                {
                    replaced = node.Key;
                }
            }

            return new NodeCheckSummary(active.Count, down, replaced);
        }

        /// <returns>True if the node's hosts were switched to the reserve.</returns>
        private async Task<bool> ReplaceOrReportAsync(
            string node,
            IReadOnlyList<PanelHost> nodeHosts,
            IReadOnlyList<PanelHost> allHosts,
            bool reserveTaken,
            NodeGuardOptions settings,
            CancellationToken cancellationToken)
        {
            string reserve = settings.ReserveTag;
            string manually = $"Замена вручную: replace-node.sh {node} {reserve} (runbook node-blocked).";
            PanelHost probe = nodeHosts[0];

            // 1. Other Russian networks: one data center may have its own route trouble.
            RemoteProbeResult remote;
            try
            {
                remote = await remoteProbes.TcpAsync(probe.Address, probe.Port, settings.RemoteCountry, settings.RemoteProbes, cancellationToken);
            }
            catch (RemoteProbesUnavailableException ex)
            {
                logger.LogWarning(ex, "Node guard: Globalping gave no answer for {Node}", node);
                remote = new RemoteProbeResult(0, 0);
            }

            if (remote.Probes == 0)
            {
                return await ReportAsync(AlertSeverity.Critical, node, "unconfirmed",
                    $"Нода {node} не отвечает из нашего ДЦ {settings.FailuresInARow} проверки подряд, но подтвердить блокировку через Globalping не удалось. Автозамена не выполнена. {manually}",
                    cancellationToken);
            }
            if (remote.Reached * 2 > remote.Probes)
            {
                return await ReportAsync(AlertSeverity.Warning, node, "route",
                    $"Нода {node} не отвечает из нашего ДЦ, но {remote.Reached} из {remote.Probes} проб Globalping в РФ до неё достают: блокировки нет, замена не нужна. Проверьте маршрут управляющего VPS.",
                    cancellationToken);
            }

            // 2. Alive abroad? A node the panel cannot see either is down, not blocked: the reserve would not explain it.
            IReadOnlyList<PanelNode> nodes;
            try
            {
                nodes = await inventory.GetNodesAsync(cancellationToken);
            }
            catch (PanelException ex)
            {
                return await ReportAsync(AlertSeverity.Critical, node, "panel",
                    $"Нода {node} недоступна из РФ, но панель не ответила ({ex.Message}). Автозамена не выполнена. {manually}",
                    cancellationToken);
            }

            PanelNode? panelNode = nodes.FirstOrDefault(n => n.Name.Equals(node, StringComparison.OrdinalIgnoreCase));
            if (panelNode is null)
            {
                return await ReportAsync(AlertSeverity.Critical, node, "unknown",
                    $"Нода {node} недоступна из РФ, но в панели нет ноды с именем {node} (имя ноды должно совпадать с тегом её хостов). Автозамена не выполнена. {manually}",
                    cancellationToken);
            }
            if (!panelNode.IsConnected)
            {
                return await ReportAsync(AlertSeverity.Critical, node, "dead",
                    $"Нода {node} недоступна и из РФ, и для панели — это сбой ноды, а не блокировка: перезапустите или пересоздайте её (runbook node-blocked). Хосты не переключались.",
                    cancellationToken);
            }

            string blocked = $"Нода {node} заблокирована из РФ: недоступна из нашего ДЦ и для {remote.Probes - remote.Reached} из {remote.Probes} проб Globalping, а панель видит её живой.";
            if (!settings.AutoReplace)
            {
                return await ReportAsync(AlertSeverity.Critical, node, "blocked",
                    $"{blocked} Автозамена выключена (NodeGuard__AutoReplace). {manually}",
                    cancellationToken);
            }

            // 3. Guards: one automatic replacement per cooldown, and a reserve that is free, connected and reachable.
            DateTime now = clock.UtcNow;
            ServiceSetting? last = await db.Settings.FirstOrDefaultAsync(s => s.Key == ServiceSetting.NodeGuardLastReplacement, cancellationToken);
            if (last is not null && now - last.UpdatedAt < TimeSpan.FromHours(settings.CooldownHours))
            {
                return await ReportAsync(AlertSeverity.Critical, node, "cooldown",
                    $"{blocked} Но автозамена уже была меньше {settings.CooldownHours} ч назад ({last.Value}). {manually}",
                    cancellationToken);
            }

            List<PanelHost> reserveHosts = allHosts.Where(h => h.Tags.Contains(reserve, StringComparer.OrdinalIgnoreCase)).ToList();
            if (reserveHosts.Count == 0)
            {
                return await ReportAsync(AlertSeverity.Critical, node, "no-reserve",
                    $"{blocked} А резерва нет (хостов с тегом {reserve}). Поднимите резерв или замените ноду вручную (runbook node-blocked).",
                    cancellationToken);
            }
            if (reserveTaken || reserveHosts.Any(h => !h.IsDisabled))
            {
                return await ReportAsync(AlertSeverity.Critical, node, "reserve-busy",
                    $"{blocked} А резерв {reserve} уже занят. Поднимите новый резерв (ansible-playbook playbooks/node.yml) и замените вручную (runbook node-blocked).",
                    cancellationToken);
            }

            PanelNode? reserveNode = nodes.FirstOrDefault(n => n.Name.Equals(reserve, StringComparison.OrdinalIgnoreCase));
            if (reserveNode is not { IsConnected: true, IsDisabled: false })
            {
                return await ReportAsync(AlertSeverity.Critical, node, "reserve-offline",
                    $"{blocked} А резервная нода {reserve} не подключена к панели или выключена. Автозамена не выполнена. {manually}",
                    cancellationToken);
            }
            if (!await AnyConnectsAsync(reserveHosts.Select(h => (h.Address, h.Port)), cancellationToken))
            {
                return await ReportAsync(AlertSeverity.Critical, node, "reserve-blocked",
                    $"{blocked} А резерв {reserve} тоже не отвечает из нашего ДЦ. Автозамена не выполнена: нужен другой резерв.",
                    cancellationToken);
            }

            // 4. The reserve comes on first and the blocked node goes off after: users are never left with neither.
            try
            {
                await inventory.SetHostsDisabledAsync(reserveHosts.Select(h => h.Uuid).ToList(), disabled: false, cancellationToken);
                await inventory.SetHostsDisabledAsync(nodeHosts.Select(h => h.Uuid).ToList(), disabled: true, cancellationToken);
            }
            catch (PanelException ex)
            {
                return await ReportAsync(AlertSeverity.Critical, node, "switch-failed",
                    $"Автозамена ноды {node} на {reserve} не удалась: {ex.Message}. Проверьте хосты в панели: возможно, резерв уже включён, а {node} — нет. {manually}",
                    cancellationToken);
            }

            string value = string.Create(CultureInfo.InvariantCulture, $"{now:O} {node} → {reserve}");
            if (last is null)
            {
                db.Settings.Add(ServiceSetting.Create(ServiceSetting.NodeGuardLastReplacement, value, null, now));
            }
            else
            {
                last.Set(value, null, now);
            }
            db.AuditLog.Add(AuditLogEntry.BySystem(
                AuditActions.NodeAutoReplace,
                null,
                Json.Serialize(new
                {
                    blocked = node,
                    reserve,
                    disabledHosts = nodeHosts.Select(h => h.Remark),
                    enabledHosts = reserveHosts.Select(h => h.Remark),
                    remoteProbes = remote.Probes,
                    remoteReached = remote.Reached,
                }),
                now));
            await db.SaveChangesAsync(cancellationToken);

            await alerts.RaiseAsync(new Alert(
                AlertSeverity.Critical,
                $"node-guard:{node}:replaced",
                $"🔁 Автозамена ноды {node}. {blocked} Хосты {node} выключены, хосты резерва {reserve} включены. Клиенты подтянут новый адрес при обновлении подписки (до 2 ч); если сбой массовый — разошлите «Инцидент» (/broadcast). Дальше: пополнить резерв (ansible-playbook playbooks/node.yml) и разобрать блокировку (runbook node-blocked).",
                AlertAudience.TechAdminAndAdmin),
                cancellationToken);
            logger.LogWarning("Node guard switched {Node} to the reserve {Reserve}", node, reserve);
            return true;
        }

        private async Task<bool> ReportAsync(AlertSeverity severity, string node, string reason, string text, CancellationToken cancellationToken)
        {
            await AlertAsync(severity, node, reason, text, cancellationToken);
            return false;
        }

        private Task AlertAsync(AlertSeverity severity, string node, string reason, string text, CancellationToken cancellationToken) =>
            alerts.RaiseAsync(new Alert(severity, $"node-guard:{node}:{reason}", text), cancellationToken);

        private async Task<bool> AnyConnectsAsync(IEnumerable<(string Host, int Port)> targets, CancellationToken cancellationToken) =>
            (await ProbeAsync(targets, cancellationToken)).ContainsValue(true);

        private async Task<Dictionary<(string Host, int Port), bool>> ProbeAsync(IEnumerable<(string Host, int Port)> targets, CancellationToken cancellationToken)
        {
            List<(string Host, int Port)> distinct = targets.Distinct().ToList();
            bool[] results = await Task.WhenAll(distinct.Select(t => localProbe.CanConnectAsync(t.Host, t.Port, cancellationToken)));
            return distinct.Zip(results).ToDictionary(r => r.First, r => r.Second);
        }

        /// <summary><c>ya.ru:443</c> → (ya.ru, 443); without a port — 443.</summary>
        internal static (string Host, int Port) ParseTarget(string target)
        {
            int colon = target.LastIndexOf(':');
            return colon > 0 && int.TryParse(target.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                ? (target[..colon], port)
                : (target, 443);
        }
    }
}
