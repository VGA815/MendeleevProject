using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Payments.Webhook;
using Mendeleev.Infrastructure.Payments.Fake;
using Mendeleev.SharedKernel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mendeleev.Web.Pages.Dev
{
    /// <summary>
    /// The fake aggregator's payment page: pays or cancels and delivers the signed webhook through the same
    /// command a real aggregator's webhook goes through. «Без вебхука» exercises the 5-minute check (FR-PAY-07).
    /// Returns 404 unless <c>Payments:Fake:Enabled</c>.
    /// </summary>
    public sealed class FakePayModel(
        IServiceProvider services,
        ICommandHandler<HandlePaymentWebhookCommand> webhook) : PageModel
    {
        public FakePaymentView? Payment { get; private set; }

        public string? Message { get; private set; }

        public IActionResult OnGet(Guid orderId)
        {
            if (services.GetService<IFakePaymentSimulator>() is not IFakePaymentSimulator simulator)
            {
                return NotFound();
            }

            Payment = simulator.Find(orderId);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(Guid orderId, string action, CancellationToken cancellationToken)
        {
            if (services.GetService<IFakePaymentSimulator>() is not IFakePaymentSimulator simulator)
            {
                return NotFound();
            }

            ProviderPaymentState state = action == "cancel" ? ProviderPaymentState.Canceled : ProviderPaymentState.Succeeded;
            WebhookRequest? request = simulator.Simulate(orderId, state);

            if (request is not null && action != "pay-silent")
            {
                Result result = await webhook.Handle(new HandlePaymentWebhookCommand(FakePaymentProviderCode, request), cancellationToken);
                Message = result.IsSuccess ? "Вебхук доставлен." : $"Вебхук отклонён: {result.Error.Description}";
            }
            else if (request is not null)
            {
                Message = "Оплачено без вебхука: статус подхватит быстрая проверка (раз в 5 минут) или кнопка «Проверить оплату».";
            }

            Payment = simulator.Find(orderId);
            return Page();
        }

        private const string FakePaymentProviderCode = "fake";
    }
}
