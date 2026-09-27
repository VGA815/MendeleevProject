using Mendeleev.Application.Abstractions.Payments;

namespace Mendeleev.Web.Endpoints.Webhooks
{
    /// <summary>Reads a webhook with a hard body limit (ТЗ 31, «Защита вебхуков»: «ограничение размера тела»).</summary>
    internal static class WebhookRequestReader
    {
        public const int MaxBodyBytes = 256 * 1024;

        /// <returns>Null if the body is larger than the limit.</returns>
        public static async Task<WebhookRequest?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            if (request.ContentLength > MaxBodyBytes)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            byte[] chunk = new byte[16 * 1024];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxBodyBytes)
                {
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in request.Headers)
            {
                headers[header.Key] = header.Value.ToString();
            }

            return new WebhookRequest(headers, buffer.ToArray(), request.HttpContext.Connection.RemoteIpAddress?.ToString());
        }
    }
}
