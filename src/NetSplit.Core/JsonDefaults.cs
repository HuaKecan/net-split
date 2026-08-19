using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetSplit.Core;

public static class JsonDefaults
{
    public static JsonSerializerOptions Create(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = writeIndented
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
