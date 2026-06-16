using DesensitizeProxy.Core.Models;

namespace DesensitizeProxy.AspNetCore.Auth;

public static class MultiAuthHandler
{
    public static IReadOnlyDictionary<string, string> ResolveAuthHeaders(UpstreamTarget target)
    {
        var provider = target.Provider?.ToLowerInvariant();
        return provider switch
        {
            null or "openai" or "openai-compatible" or "deepseek" => new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {Expand(target.ApiKey)}"
            },
            "anthropic" => new Dictionary<string, string>
            {
                ["x-api-key"] = Expand(target.ApiKey),
                ["anthropic-version"] = "2023-06-01"
            },
            "google" or "gemini" or "vertex" => new Dictionary<string, string>
            {
                ["x-goog-api-key"] = Expand(target.ApiKey)
            },
            _ => throw new NotSupportedException($"Provider '{target.Provider}' is not supported")
        };
    }

    private static string Expand(string value)
    {
        if (value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}'))
        {
            var name = value[2..^1];
            return Environment.GetEnvironmentVariable(name) ?? string.Empty;
        }

        return Environment.ExpandEnvironmentVariables(value);
    }
}
