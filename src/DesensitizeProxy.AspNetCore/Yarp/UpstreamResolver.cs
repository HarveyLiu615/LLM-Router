using System.Text.Json.Nodes;
using DesensitizeProxy.Core.Models;

namespace DesensitizeProxy.AspNetCore.Yarp;

public sealed class UpstreamResolver
{
    public UpstreamTarget Resolve(HttpContext context, JsonNode? request, PrivacyConfig config)
    {
        if (config.Proxy.Targets.Count == 0)
        {
            throw new InvalidOperationException("No upstream targets configured");
        }

        var protocol = DetectProtocol(context.Request.Path, request);
        if (protocol is not null && TryFindTargetByProvider(config, protocol, out var protocolTarget))
        {
            return protocolTarget;
        }

        if (!string.IsNullOrWhiteSpace(config.Proxy.DefaultTarget) &&
            config.Proxy.Targets.TryGetValue(config.Proxy.DefaultTarget, out var defaultTarget))
        {
            return defaultTarget;
        }

        if (config.Proxy.Targets.TryGetValue("default", out defaultTarget))
        {
            return defaultTarget;
        }

        if (config.Proxy.Targets.Count == 1)
        {
            return config.Proxy.Targets.Values.Single();
        }

        throw new InvalidOperationException("Multiple upstream targets configured. Set PrivacyProxy:Proxy:DefaultTarget or define a target named 'default'.");
    }

    private static string? DetectProtocol(PathString path, JsonNode? request)
    {
        var pathValue = path.Value ?? string.Empty;
        if (pathValue.Contains("/messages", StringComparison.OrdinalIgnoreCase))
        {
            return "anthropic";
        }

        if (pathValue.Equals("/v1beta/models", StringComparison.OrdinalIgnoreCase) ||
            pathValue.Contains("generateContent", StringComparison.OrdinalIgnoreCase))
        {
            return "gemini";
        }

        if (request?["contents"] is not null || request?["systemInstruction"] is not null)
        {
            return "gemini";
        }

        if (pathValue.Contains("/responses", StringComparison.OrdinalIgnoreCase) ||
            pathValue.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            request?["input"] is not null)
        {
            return "openai";
        }

        return null;
    }

    private static bool TryFindTargetByProvider(PrivacyConfig config, string protocol, out UpstreamTarget target)
    {
        foreach (var candidate in config.Proxy.Targets.Values)
        {
            if (ProviderMatches(candidate.Provider, protocol))
            {
                target = candidate;
                return true;
            }
        }

        target = null!;
        return false;
    }

    private static bool ProviderMatches(string? provider, string protocol)
    {
        provider ??= "openai-compatible";
        return protocol switch
        {
            "openai" => provider.Equals("openai", StringComparison.OrdinalIgnoreCase) ||
                        provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase),
            "anthropic" => provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase),
            "gemini" => provider.Equals("gemini", StringComparison.OrdinalIgnoreCase) ||
                        provider.Equals("google", StringComparison.OrdinalIgnoreCase) ||
                        provider.Equals("vertex", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
