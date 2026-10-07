using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Payments.Check;
using Mendeleev.Application.Payments.Latest;
using Mendeleev.Domain.Payments;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Cabinet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Mendeleev.Web.Pages.Pay
{
    /// <summary>
    /// Return from the aggregator (ТЗ 27, «Кабинет»). A cabinet user sees «Проверяем оплату…», the page polls every
    /// 3 seconds up to 60 and shows the result; if the payment is still not confirmed — «Проверить ещё раз» and
    /// support. A bot user, who has no cabinet session, is sent back to the bot. The payment id is in the path
    /// (<c>/pay/return/{id}</c>), <c>?paymentId=</c> still works for links handed out before. TryBit returns everyone
    /// to the project's one address, <c>/pay/return</c>: a cabinet user is taken to the latest payment from there.
    /// </summary>
    public sealed class ReturnModel(
        ICommandHandler<CheckPaymentCommand, PaymentCheckResult> checkPayment,
        IQueryHandler<GetLatestPaymentQuery, Guid> latestPayment)
        : PageModel
    {
        public Guid? PaymentId { get; private set; }

        public PaymentCheckResult? Check { get; private set; }

        public async Task<IActionResult> OnGetAsync(Guid? paymentId, CancellationToken cancellationToken)
        {
            if (User.Identity?.IsAuthenticated != true)
            {
                return Page();
            }

            if (paymentId is not Guid id)
            {
                Result<Guid> latest = await latestPayment.Handle(new GetLatestPaymentQuery(User.GetUserId()), cancellationToken);
                return latest.IsSuccess ? RedirectToPage(new { paymentId = latest.Value }) : Page();
            }

            // Someone else's payment id is simply not found: the check is by user and payment together.
            Result<PaymentCheckResult> result = await checkPayment.Handle(new CheckPaymentCommand(User.GetUserId(), id), cancellationToken);
            if (result.IsSuccess)
            {
                PaymentId = id;
                Check = result.Value;
            }

            return Page();
        }

        public async Task<IActionResult> OnGetStatusAsync(Guid paymentId, CancellationToken cancellationToken)
        {
            if (User.Identity?.IsAuthenticated != true)
            {
                return Unauthorized();
            }

            Result<PaymentCheckResult> result = await checkPayment.Handle(new CheckPaymentCommand(User.GetUserId(), paymentId), cancellationToken);
            return result.IsSuccess
                ? new JsonResult(new { state = StateOf(result.Value), accessPending = result.Value.AccessPending })
                : NotFound();
        }

        public static string StateOf(PaymentCheckResult check) => check.Status switch
        {
            PaymentStatus.Succeeded or PaymentStatus.Refunded => "succeeded",
            PaymentStatus.Canceled or PaymentStatus.Failed => "canceled",
            _ => "pending",
        };
    }
}
