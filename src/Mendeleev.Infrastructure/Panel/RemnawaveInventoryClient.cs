using System.Net.Http.Json;
using Mendeleev.Application.Abstractions.Panel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Mendeleev.Infrastructure.Panel
{
    /// <summary>
    /// <see cref="IPanelInventory"/> over the Remnawave REST API with its own token (<c>Remnawave:InventoryToken</c>,
    /// scopes <c>nodes:read</c> and <c>hosts:*</c>): the same calls replace-node.sh makes (FR-NODE-18).
    /// </summary>
    internal sealed class RemnawaveInventoryClient(
        HttpClient http,
        IOptions<RemnawaveOptions> options,
        ILogger<RemnawaveInventoryClient> logger)
        : IPanelInventory
    {
        public const string HttpClientName = "remnawave-inventory";

        public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.InventoryToken);

        public async Task<IReadOnlyList<PanelNode>> GetNodesAsync(CancellationToken cancellationToken)
        {
            List<RemnawaveNodeDto> nodes = await SendAsync<List<RemnawaveNodeDto>>(HttpMethod.Get, "api/nodes", null, "list nodes", cancellationToken);
            return nodes.Select(n => new PanelNode(n.Uuid, n.Name, n.IsConnected, n.IsDisabled)).ToList();
        }

        public async Task<IReadOnlyList<PanelHost>> GetHostsAsync(CancellationToken cancellationToken)
        {
            List<RemnawaveHostDto> hosts = await SendAsync<List<RemnawaveHostDto>>(HttpMethod.Get, "api/hosts", null, "list hosts", cancellationToken);
            return hosts.Select(h => new PanelHost(h.Uuid, h.Remark, h.Address, h.Port, h.IsDisabled, h.Tags ?? [])).ToList();
        }

        public async Task SetHostsDisabledAsync(IReadOnlyCollection<Guid> hostUuids, bool disabled, CancellationToken cancellationToken)
        {
            if (hostUuids.Count == 0)
            {
                return;
            }

            string path = disabled ? "api/hosts/bulk/disable" : "api/hosts/bulk/enable";
            await SendAsync<object>(HttpMethod.Post, path, new HostUuidsRequest(hostUuids), disabled ? "disable hosts" : "enable hosts", cancellationToken, readBody: false);
        }

        private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string operation, CancellationToken cancellationToken, bool readBody = true)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: RemnawaveClient.JsonOptions);
            }

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
                                           or TaskCanceledException { InnerException: TimeoutException })
            {
                throw new PanelUnavailableException($"Panel request {method} {path} failed: {ex.Message}", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    string text = await response.Content.ReadAsStringAsync(cancellationToken);
                    text = text.Length > 500 ? text[..500] : text;
                    int status = (int)response.StatusCode;
                    if (status >= 500 || status == 429)
                    {
                        throw new PanelUnavailableException($"Panel {operation} returned {status}: {text}");
                    }

                    logger.LogError("Panel rejected {Operation} with {Status}: {Body}", operation, status, text);
                    throw new PanelContractException($"Panel {operation} returned {status}: {text}");
                }

                if (!readBody)
                {
                    return default!;
                }

                RemnawaveEnvelope<T>? envelope = await response.Content.ReadFromJsonAsync<RemnawaveEnvelope<T>>(RemnawaveClient.JsonOptions, cancellationToken);
                return envelope is { Response: not null }
                    ? envelope.Response
                    : throw new PanelContractException($"Panel returned an empty body for {operation}.");
            }
        }

        private sealed record HostUuidsRequest(IReadOnlyCollection<Guid> Uuids);

        private sealed record RemnawaveNodeDto(Guid Uuid, string Name, bool IsConnected, bool IsDisabled);

        private sealed record RemnawaveHostDto(Guid Uuid, string Remark, string Address, int Port, bool IsDisabled, List<string>? Tags);
    }
}
