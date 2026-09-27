using System.Net;

namespace Mendeleev.Infrastructure.Http
{
    /// <summary>
    /// Builds the primary handler for clients that must reach services through a proxy abroad: Bot API
    /// from a Russian DC (ТЗ 26, «Доступ к Bot API»), alert channels. HTTP and SOCKS5 are supported.
    /// </summary>
    internal static class ProxyHandlerFactory
    {
        public static HttpMessageHandler Create(string? proxyUrl)
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };

            if (!string.IsNullOrWhiteSpace(proxyUrl))
            {
                var uri = new Uri(proxyUrl);
                var proxy = new WebProxy(new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri);
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    string[] parts = uri.UserInfo.Split(':', 2);
                    proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
                }

                handler.Proxy = proxy;
                handler.UseProxy = true;
            }

            return handler;
        }
    }
}
