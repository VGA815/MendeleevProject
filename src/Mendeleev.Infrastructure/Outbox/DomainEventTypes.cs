using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mendeleev.Domain.Users;
using Mendeleev.SharedKernel;

namespace Mendeleev.Infrastructure.Outbox
{
    /// <summary>
    /// Maps domain event types to the stable names stored in the outbox. The short type name is used, so
    /// moving an event to another namespace does not orphan messages already in the table.
    /// </summary>
    internal static class DomainEventTypes
    {
        public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };

        private static readonly FrozenDictionary<string, Type> ByName = typeof(User).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IDomainEvent).IsAssignableFrom(t))
            .ToFrozenDictionary(t => t.Name, StringComparer.Ordinal);

        public static string NameOf(Type eventType) =>
            ByName.ContainsKey(eventType.Name)
                ? eventType.Name
                : throw new InvalidOperationException($"Domain event {eventType} is not from the domain assembly.");

        public static Type? Resolve(string name) => ByName.GetValueOrDefault(name);
    }
}
