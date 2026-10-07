using System.Text.Json;

namespace Mendeleev.Infrastructure.Payments.TryBit
{
    /// <summary>TryBit answers <c>{ status: "success", result }</c>, or <c>{ status: "error", result: { … } }</c> on failure.</summary>
    internal sealed record TryBitAnswer(string? Status, JsonElement Result);

    /// <summary><c>POST invoice/create</c>. The amount is in <see cref="Currency"/>, TryBit converts it to cryptocurrency.</summary>
    internal sealed record TryBitCreateInvoiceRequest(
        string ShopId,
        decimal Amount,
        string Currency,
        string OrderId,
        TryBitInvoiceFields AddFields);

    internal sealed record TryBitInvoiceFields(TryBitTimeToPay TimeToPay);

    internal sealed record TryBitTimeToPay(int Hours, int Minutes);

    /// <summary><c>POST invoice/merchant/info</c>: up to 100 numbers, <c>INV-XXXXXXXX</c> or <c>XXXXXXXX</c>.</summary>
    internal sealed record TryBitInvoiceInfoRequest(string[] Uuids);

    /// <summary>
    /// The invoice as <c>invoice/create</c> and <c>invoice/merchant/info</c> return it (only what is used). It also
    /// carries the payment address, the transaction hashes and the payer's masked email: none of them is read or
    /// kept (FR-PAY-05). <see cref="OrderId"/> is the text <c>None</c> when an invoice has no order id.
    /// </summary>
    internal sealed record TryBitInvoice(
        string? Uuid,
        string? Link,
        string? Status,
        decimal? AmountInFiat,
        string? FiatCurrency,
        string? OrderId,
        bool? TestMode);

    /// <summary>
    /// The POSTBACK, as JSON or as a form, whichever the project is set to. Its amount and currency are those of the
    /// cryptocurrency sent, and the rest of it (<c>invoice_info</c>) is not covered by the token: neither is read.
    /// </summary>
    internal sealed record TryBitPostback(string? InvoiceId, string? OrderId, string? Token);
}
