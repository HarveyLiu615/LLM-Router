using System.Text.Json.Nodes;
using DesensitizeProxy.Core.Abstractions;

namespace DesensitizeProxy.Core.Redaction;

public sealed class SchemaCleaner : ISchemaCleaner
{
    private static readonly HashSet<string> CommonUnsupportedKeywords = new(StringComparer.Ordinal)
    {
        "patternProperties", "$schema", "$id",
        "$ref", "$defs", "definitions", "examples",
        "minLength", "maxLength", "minimum", "maximum",
        "multipleOf", "pattern", "format",
        "minItems", "maxItems", "uniqueItems",
        "minProperties", "maxProperties"
    };

    public JsonNode Clean(JsonNode tools, string provider)
    {
        var unsupported = ResolveUnsupportedKeywords(provider);
        var clone = tools.DeepClone();
        StripKeywords(clone, unsupported);
        return clone;
    }

    private static HashSet<string> ResolveUnsupportedKeywords(string provider)
    {
        var unsupported = new HashSet<string>(CommonUnsupportedKeywords, StringComparer.Ordinal);
        if (provider.Equals("gemini", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("google", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("vertex", StringComparison.OrdinalIgnoreCase))
        {
            unsupported.Add("additionalProperties");
        }

        return unsupported;
    }

    private static void StripKeywords(JsonNode? node, HashSet<string> unsupported)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kvp => kvp.Key).ToArray())
            {
                if (unsupported.Contains(key))
                {
                    obj.Remove(key);
                    continue;
                }

                StripKeywords(obj[key], unsupported);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                StripKeywords(child, unsupported);
            }
        }
    }
}
