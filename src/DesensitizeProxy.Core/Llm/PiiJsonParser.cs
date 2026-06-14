using System.Text.Json;
using System.Text.RegularExpressions;
using DesensitizeProxy.Core.Models;

namespace DesensitizeProxy.Core.Llm;

public sealed class PiiJsonParser
{
    public IReadOnlyList<PiiItem>? Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var json = Normalize(raw);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var items = new List<PiiItem>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryGetString(element, "type", out var type) ||
                    !TryGetString(element, "value", out var value) ||
                    string.IsNullOrWhiteSpace(type) ||
                    string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                items.Add(new PiiItem(type, value));
            }

            return items;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = prop.GetString() ?? string.Empty;
        return true;
    }

    private static string Normalize(string raw)
    {
        var json = Regex.Replace(raw.Trim(), "^```(?:json)?\\s*|\\s*```$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var start = json.IndexOf('[');
        var end = json.LastIndexOf(']');
        if (start >= 0)
        {
            json = end >= start ? json[start..(end + 1)] : json[start..] + "]";
        }

        json = Regex.Replace(json, "'([^'\\r\\n]*)'", "\"$1\"");
        json = Regex.Replace(json, @",\s*([}\]])", "$1");
        return json;
    }
}
