using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Application.Devices.GetDevices;
using Mendeleev.Application.Devices.ResetDevices;
using Mendeleev.SharedKernel;
using Mendeleev.Web.Cabinet;
using Microsoft.AspNetCore.Mvc;

namespace Mendeleev.Web.Pages.Cabinet
{
    /// <summary>
    /// Devices and «Сбросить все» with the same limit as in the bot — twice in 30 days (FR-WEB-06,
    /// FR-PNL-10). The reset asks for a confirmation first, without scripts.
    /// </summary>
    public sealed class DevicesModel(
        IQueryHandler<GetDevicesQuery, DevicesView> getDevices,
        ICommandHandler<ResetDevicesCommand, DevicesResetResult> resetDevices)
        : CabinetPageModel
    {
        public DevicesView? Devices { get; private set; }

        public string? Error { get; private set; }

        public bool Confirming { get; private set; }

        public DevicesResetResult? Reset { get; private set; }

        public async Task OnGetAsync(bool confirm, CancellationToken cancellationToken)
        {
            await LoadAsync(cancellationToken);
            Confirming = confirm && Devices is { NextResetAvailableAtUtc: null, Devices.Count: > 0 };
        }

        public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
        {
            Result<DevicesResetResult> result = await resetDevices.Handle(new ResetDevicesCommand(UserId), cancellationToken);
            if (result.IsSuccess)
            {
                Reset = result.Value;
            }
            else
            {
                Error = result.Error.Description;
            }

            await LoadAsync(cancellationToken);
            return Page();
        }

        private async Task LoadAsync(CancellationToken cancellationToken)
        {
            Result<DevicesView> result = await getDevices.Handle(new GetDevicesQuery(UserId), cancellationToken);
            if (result.IsSuccess)
            {
                Devices = result.Value;
            }
            else
            {
                Error ??= result.Error.Description;
            }
        }
    }
}
