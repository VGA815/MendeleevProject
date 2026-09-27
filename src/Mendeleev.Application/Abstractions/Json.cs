using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Mendeleev.Application.Abstractions
{
    /// <summary>Serialization for the JSON columns (audit details, notification values, payment journal).</summary>
    public static class Json
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic),
        };

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

        public static T? Deserialize<T>(string? json) =>
            string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
    }
}
