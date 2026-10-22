namespace Mendeleev.Application.Abstractions.Probes
{
    /// <summary>
    /// A TCP connection from this server — the management VPS sits in a Russian data center, so it sees what a
    /// Russian network sees (FR-NODE-18; решение 07.10: пробы из самого сервиса).
    /// </summary>
    public interface ILocalProbe
    {
        /// <returns>True if the connection was established within the timeout.</returns>
        Task<bool> CanConnectAsync(string host, int port, CancellationToken cancellationToken);
    }

    /// <summary>
    /// TCP connections from several probes of another network (Globalping), to confirm what <see cref="ILocalProbe"/>
    /// saw: one data center may have its own route trouble.
    /// </summary>
    public interface IRemoteProbes
    {
        /// <exception cref="RemoteProbesUnavailableException">The probe network did not give an answer.</exception>
        Task<RemoteProbeResult> TcpAsync(string host, int port, string country, int probes, CancellationToken cancellationToken);
    }

    /// <param name="Probes">How many probes answered with a result.</param>
    /// <param name="Reached">How many of them connected.</param>
    public sealed record RemoteProbeResult(int Probes, int Reached);

    public sealed class RemoteProbesUnavailableException(string message, Exception? innerException = null)
        : Exception(message, innerException);
}
