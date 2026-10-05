using System.Text.Json.Serialization;

namespace Mendeleev.Infrastructure.Payments.Lava
{
    /// <summary>Lava answers <c>{ data, status, status_check }</c>, or <c>{ error, data: null }</c> on failure.</summary>
    internal sealed record LavaEnvelope<T>(T? Data, string? Error)
        where T : class;

    /// <summary><c>POST /business/invoice/create</c>; the property order is the body's order.</summary>
    internal sealed record LavaCreateInvoiceRequest(
        decimal Sum,
        string OrderId,
        string ShopId,
        string HookUrl,
        string SuccessUrl,
        string FailUrl,
        int Expire,
        string Comment,
        string[]? IncludeService);

    internal sealed record LavaInvoiceStatusRequest(string ShopId, string OrderId);

    /// <summary>The invoice as <c>invoice/create</c> and <c>invoice/status</c> return it (only what is used).</summary>
    internal sealed record LavaInvoice(string? Id, string? Url, string? Status, decimal? Amount);

    /// <summary>
    /// The invoice webhook. It also carries <c>payer_details</c> (a masked card number), <c>credited</c>,
    /// <c>pay_service</c>, <c>pay_time</c> and <c>custom_fields</c>: none of them is read or kept (FR-PAY-05).
    /// </summary>
    internal sealed record LavaWebhook(
        [property: JsonPropertyName("invoice_id")] string? InvoiceId,
        [property: JsonPropertyName("order_id")] string? OrderId,
        string? Status,
        decimal? Amount);
}
