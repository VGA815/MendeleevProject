namespace Mendeleev.Application.Abstractions.Panel
{
    /// <summary>
    /// Nodes and hosts of the panel, for the automatic replacement of a node blocked from Russia (FR-NODE-18). A
    /// token of its own, apart from the service's one (users only): this one reads nodes and switches hosts on and
    /// off — scopes <c>nodes:read</c> and <c>hosts:*</c> (docs/staging.md, step 5).
    /// </summary>
    public interface IPanelInventory
    {
        /// <summary>False when no inventory token is set: the node guard then only reports.</summary>
        bool IsConfigured { get; }

        /// <exception cref="PanelUnavailableException">The panel did not answer.</exception>
        Task<IReadOnlyList<PanelNode>> GetNodesAsync(CancellationToken cancellationToken);

        /// <exception cref="PanelUnavailableException">The panel did not answer.</exception>
        Task<IReadOnlyList<PanelHost>> GetHostsAsync(CancellationToken cancellationToken);

        /// <summary>Hosts are what users connect to: a disabled host leaves the subscriptions at their next update.</summary>
        Task SetHostsDisabledAsync(IReadOnlyCollection<Guid> hostUuids, bool disabled, CancellationToken cancellationToken);
    }

    /// <param name="IsConnected">The panel abroad sees the node: it is alive outside Russia.</param>
    public sealed record PanelNode(Guid Uuid, string Name, bool IsConnected, bool IsDisabled);

    /// <param name="Tags">The node code is among them (STG1, FI1, RES1…), as replace-node.sh expects.</param>
    public sealed record PanelHost(Guid Uuid, string Remark, string Address, int Port, bool IsDisabled, IReadOnlyList<string> Tags)
    {
        /// <summary>The node this host belongs to; a host without tags is not managed.</summary>
        public string? NodeCode => Tags.FirstOrDefault();
    }
}
