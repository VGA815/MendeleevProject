using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Mendeleev.Web.Infrastructure
{
    /// <summary>
    /// Any unexpected failure becomes 500 — for webhooks this is what makes the aggregator and Telegram
    /// retry (ТЗ 23: «БД недоступна при вебхуке — HTTP 500, агрегатор повторит»).
    /// </summary>
    internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
            logger.LogError(exception, "Unhandled exception (traceId: {TraceId})", traceId);

            if (httpContext.Response.HasStarted)
            {
                return false;
            }

            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await httpContext.Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Server failure",
                    Extensions = { ["traceId"] = traceId },
                },
                cancellationToken);

            return true;
        }
    }
}
