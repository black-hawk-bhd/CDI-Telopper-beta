using System.Text.Json;
using System.Text.Json.Serialization;

namespace EEWTelop.Infrastructure.Persistence;

internal static class JsonFileOptions
{
    public static JsonSerializerOptions Create() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
