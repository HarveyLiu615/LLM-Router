using System.Text.Json.Nodes;

namespace DesensitizeProxy.Core.Abstractions;

public interface ISchemaCleaner
{
    JsonNode Clean(JsonNode tools, string provider);
}
