namespace Mendeleev.Application.Configuration
{
    /// <summary>
    /// The automatic replacement of a node blocked from Russia (FR-NODE-18; ТЗ 25, «Замена ноды»; решение 07.10): the
    /// service probes the nodes' hosts from its own Russian data center, confirms through Globalping and the panel,
    /// and switches the blocked node's hosts to the warm reserve.
    /// </summary>
    public sealed class NodeGuardOptions
    {
        public const string SectionName = "NodeGuard";

        /// <summary>The probes, their metrics and the alerts; needs the inventory token (<c>Remnawave:InventoryToken</c>).</summary>
        public bool Enabled { get; init; }

        /// <summary>
        /// The switch to the reserve itself. Off — a confirmed block is only reported, with the command for a manual
        /// replacement: while there is no warm reserve the tech admin still learns of a block within 10 minutes.
        /// </summary>
        public bool AutoReplace { get; init; }

        /// <summary>Hosts with this tag are the warm reserve: disabled until a replacement.</summary>
        public string ReserveTag { get; init; } = "RES1";

        /// <summary>Checks in a row without a connection before anything happens — 2 at 5 minutes, «≤ 10 минут» (ТЗ 25).</summary>
        public int FailuresInARow { get; init; } = 2;

        /// <summary>At most one automatic replacement in this many hours; the next one is the tech admin's.</summary>
        public int CooldownHours { get; init; } = 6;

        /// <summary>
        /// Sites that must open from here for a check to count: without them the trouble is on our side, and nothing
        /// is replaced (host:port).
        /// </summary>
        public List<string> ControlTargets { get; init; } = ["ya.ru:443", "vk.com:443"];

        /// <summary>Globalping probes that must confirm the block, and where they are.</summary>
        public int RemoteProbes { get; init; } = 3;

        public string RemoteCountry { get; init; } = "RU";
    }
}
