using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaseAgents.Core.Models;

/// <summary>JSON settings shared by the store, the API and the worker: camelCase, enums as names, nulls omitted.</summary>
public static class CaseAgentsJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
