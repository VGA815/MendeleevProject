namespace Mendeleev.ContractTests
{
    /// <summary>
    /// Remnawave drops connections that did not come through a TLS-terminating reverse proxy. In production
    /// Caddy on the panel VPS sets these headers; here the test stands in for it.
    /// </summary>
    internal sealed class ReverseProxyHeaders : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10");
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
