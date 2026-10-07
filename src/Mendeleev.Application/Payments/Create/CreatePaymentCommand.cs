using FluentValidation;
using Mendeleev.Application.Abstractions.Messaging;

namespace Mendeleev.Application.Payments.Create
{
    /// <summary>
    /// «Оплатить»: a payment at the aggregator for the chosen tariff (FR-PAY-01), with the discount of the promo
    /// code the user entered (FR-PAY-15). An unpaid payment for the same tariff and code is shown again instead of
    /// a new one; at most 5 payments an hour per user (FR-PAY-10). If the active aggregator refuses to create the
    /// invoice, the next switched-on one gets the same payment (FR-PAY-14).
    /// </summary>
    public sealed record CreatePaymentCommand(long UserId, string TariffCode) : ICommand<PaymentLink>;

    /// <param name="PromoCode">The code whose discount is in <see cref="Amount"/>.</param>
    /// <param name="FullPrice">The price without the discount, when there is one.</param>
    /// <param name="DroppedPromoCode">
    /// The code the user had entered but which no longer applies (expired, switched off, used up): it is
    /// removed, and the payment is at the full price — the screen with the pay button says so.
    /// </param>
    public sealed record PaymentLink(
        Guid PaymentId,
        string ConfirmationUrl,
        decimal Amount,
        string TariffName,
        int Days,
        bool Reused,
        string? PromoCode = null,
        decimal? FullPrice = null,
        string? DroppedPromoCode = null);

    internal sealed class CreatePaymentCommandValidator : AbstractValidator<CreatePaymentCommand>
    {
        public CreatePaymentCommandValidator()
        {
            RuleFor(x => x.TariffCode).NotEmpty().MaximumLength(64);
        }
    }
}
