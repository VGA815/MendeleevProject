namespace Mendeleev.Application.Abstractions.Payments
{
    public interface IPaymentProviderRegistry
    {
        /// <summary>The provider new payments are created with.</summary>
        IPaymentProvider Active { get; }

        /// <summary>Any configured provider — old payments keep their provider after a switch.</summary>
        IPaymentProvider? Find(string code);

        IReadOnlyList<IPaymentProvider> All { get; }
    }
}
