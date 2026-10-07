using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Payments.Create;
using Mendeleev.Application.Payments.History;
using Mendeleev.Application.Promos;
using Mendeleev.Domain.Payments;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Cabinet;
using Microsoft.AspNetCore.Mvc;

namespace Mendeleev.Web.Pages.Cabinet
{
    /// <summary>
    /// Tariffs and payment (FR-WEB-05) through the same use case as the bot: an unpaid payment for the same
    /// tariff is shown again, not duplicated, and the prices carry the discount of the promo code the user
    /// entered here or in the bot (FR-PAY-15); below them, the user's payments (FR-PAY-17). The aggregator's page
    /// opens by a link, not by a redirect of the form: the CSP allows forms to post only to this site.
    /// </summary>
    public sealed class PayModel(
        IQueryHandler<GetOfferQuery, Offer> getOffer,
        IQueryHandler<GetPaymentHistoryQuery, IReadOnlyList<PaymentHistoryItem>> getHistory,
        ICommandHandler<CreatePaymentCommand, PaymentLink> createPayment)
        : CabinetPageModel
    {
        public Offer Offer { get; private set; } = new([], null);

        public IReadOnlyList<PaymentHistoryItem> History { get; private set; } = [];

        public PaymentLink? Payment { get; private set; }

        public string? Error { get; private set; }

        public bool PaymentsUnavailable { get; private set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            Offer = (await getOffer.Handle(new GetOfferQuery(UserId), cancellationToken)).Value;
            History = (await getHistory.Handle(new GetPaymentHistoryQuery(UserId), cancellationToken)).Value;
        }

        public async Task<IActionResult> OnPostAsync(string? tariff, CancellationToken cancellationToken)
        {
            Result<PaymentLink> result = await createPayment.Handle(new CreatePaymentCommand(UserId, tariff ?? string.Empty), cancellationToken);
            if (result.IsSuccess)
            {
                Payment = result.Value;
                return Page();
            }

            // ТЗ 27, «Обработка ошибок»: the aggregator is down → «Оплата временно недоступна» and support.
            PaymentsUnavailable = result.Error == PaymentErrors.ProviderUnavailable || result.Error == PaymentErrors.PaymentsDisabled;
            Error = PaymentsUnavailable ? "Оплата временно недоступна. Попробуйте позже или напишите в поддержку." : result.Error.Description;
            Offer = (await getOffer.Handle(new GetOfferQuery(UserId), cancellationToken)).Value;
            History = (await getHistory.Handle(new GetPaymentHistoryQuery(UserId), cancellationToken)).Value;
            return Page();
        }

        public static string StatusText(PaymentHistoryItem item)
        {
            string status = item.Status switch
            {
                PaymentStatus.Succeeded => "оплачен",
                PaymentStatus.Refunded => "возвращён",
                _ => "ждёт оплаты",
            };
            if (item.PromoCode is string code)
            {
                status += $", промокод {code}";
            }
            return item.Manual ? status + ", оплата вне системы" : status;
        }
    }
}
