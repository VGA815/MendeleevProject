using Mendeleev.Application.Abstractions.Payments;
using Mendeleev.Application.Configuration;
using Microsoft.Extensions.Options;

namespace Mendeleev.Infrastructure.Payments
{
    /// <summary>
    /// All registered aggregators; the active one comes from <c>Payments:ActiveProvider</c>. Switching to
    /// the second aggregator (stage 1.5) is a configuration change, old payments keep their provider.
    /// </summary>
    internal sealed class PaymentProviderRegistry(
        IEnumerable<IPaymentProvider> providers,
        IOptionsMonitor<PaymentOptions> options)
        : IPaymentProviderRegistry
    {
        private readonly IReadOnlyList<IPaymentProvider> _providers = providers.ToList();

        public IPaymentProvider Active =>
            Find(options.CurrentValue.ActiveProvider)
            ?? throw new InvalidOperationException($"Payment provider '{options.CurrentValue.ActiveProvider}' is not registered.");

        public IReadOnlyList<IPaymentProvider> All => _providers;

        public IPaymentProvider? Find(string code) =>
            _providers.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
    }
}
