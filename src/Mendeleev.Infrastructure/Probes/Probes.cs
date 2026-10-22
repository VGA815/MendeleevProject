using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using Mendeleev.Application.Abstractions.Probes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Mendeleev.Infrastructure.Probes
{
    public sealed class ProbeOptions
    {
        public const string SectionName = "Probes";

        /// <summary>A TCP connection that takes longer counts as failed.</summary>
        public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>Globalping API (globalping.io), the same probes as infra/scripts/ru-reachability.py.</summary>
        public string GlobalpingUrl { get; init; } = "https://api.globalping.io/v1";

        /// <summary>Optional: a token raises the free hourly limit of measurements.</summary>
        public string GlobalpingToken { get; init; } = string.Empty;

        /// <summary>How long to wait for the probes' results.</summary>
        public TimeSpan GlobalpingWait { get; init; } = TimeSpan.FromSeconds(45);
    }

    /// <summary>A TCP handshake from this server, which sits in a Russian data center (FR-NODE-18).</summary>
    internal sealed class TcpLocalProbe(IOptions<ProbeOptions> options) : ILocalProbe
    {
        public async Task<bool> CanConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Value.ConnectTimeout);
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(host, port, timeout.Token);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// TCP «ping» from Globalping probes in one country: a measurement is created, then polled until it is finished.
    /// A probe that got any answer counts as reached, as in infra/scripts/ru-reachability.py.
    /// </summary>
    internal sealed class GlobalpingRemoteProbes(HttpClient http, IOptions<ProbeOptions> options, ILogger<GlobalpingRemoteProbes> logger)
        : IRemoteProbes
    {
        public const string HttpClientName = "globalping";

        public async Task<RemoteProbeResult> TcpAsync(string host, int port, string country, int probes, CancellationToken cancellationToken)
        {
            try
            {
                using HttpResponseMessage created = await http.PostAsJsonAsync("measurements", new
                {
                    type = "ping",
                    target = host,
                    locations = new[] { new { country, limit = probes } },
                    measurementOptions = new { protocol = "TCP", port, packets = 3 },
                }, cancellationToken);
                if (!created.IsSuccessStatusCode)
                {
                    throw new RemoteProbesUnavailableException($"Globalping refused the measurement: {(int)created.StatusCode}");
                }

                string id = (await created.Content.ReadFromJsonAsync<CreatedMeasurement>(cancellationToken))?.Id
                    ?? throw new RemoteProbesUnavailableException("Globalping returned no measurement id.");

                DateTime giveUp = DateTime.UtcNow + options.Value.GlobalpingWait;
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    Measurement? measurement = await http.GetFromJsonAsync<Measurement>($"measurements/{Uri.EscapeDataString(id)}", cancellationToken);
                    if (measurement is { Status: "finished" })
                    {
                        List<MeasurementResult> results = measurement.Results ?? [];
                        int reached = results.Count(r => r.Result?.Stats?.Loss is double loss && loss < 100);
                        return new RemoteProbeResult(results.Count, reached);
                    }
                    if (DateTime.UtcNow > giveUp)
                    {
                        throw new RemoteProbesUnavailableException("Globalping did not finish in time.");
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TimeoutRejectedException or BrokenCircuitException
                                           or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Globalping measurement failed");
                throw new RemoteProbesUnavailableException($"Globalping is unavailable: {ex.Message}", ex);
            }
        }

        private sealed record CreatedMeasurement(string? Id);

        private sealed record Measurement(string? Status, List<MeasurementResult>? Results);

        private sealed record MeasurementResult(ProbeResult? Result);

        private sealed record ProbeResult(ProbeStats? Stats);

        private sealed record ProbeStats([property: JsonPropertyName("loss")] double? Loss);
    }
}
