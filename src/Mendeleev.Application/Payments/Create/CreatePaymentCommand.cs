using FluentValidation;
using Mendeleev.Application.Abstractions.Messaging;

namespace Mendeleev.Application.Payments.Create
{
    /// <summary>
    /// «Оплатить»: a payment at the aggregator for the chosen tariff (FR-PAY-01). An unpaid payment for
    /// the same tariff is shown again instead of a new one; at most 5 payments an hour per user (FR-PAY-10).
    /// </summary>
    public sealed record CreatePaymentCommand(long UserId, string TariffCode) : ICommand<PaymentLink>;

    public sealed record PaymentLink(
        Guid PaymentId,
        string ConfirmationUrl,
        decimal Amount,
        string TariffName,
        int Days,
        bool Reused);

    internal sealed class CreatePaymentCommandValidator : AbstractValidator<CreatePaymentCommand>
    {
        public CreatePaymentCommandValidator()
        {
            RuleFor(x => x.TariffCode).NotEmpty().MaximumLength(64);
        }
    }
}
