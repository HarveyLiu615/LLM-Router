using System.Text.Json.Nodes;
using DesensitizeProxy.AspNetCore.Yarp;
using DesensitizeProxy.Core.Models;
using Microsoft.AspNetCore.Http;

namespace DesensitizeProxy.Core.Tests;

public sealed class UpstreamResolverTests
{
    [Fact]
    public void Resolve_UsesOpenAiTargetForResponsesProtocol()
    {
        var target = Resolve("/v1/responses", """{"model":"gpt-5.4-mini","input":"hello"}""");

        Assert.Equal("https://openai.example/v1", target.BaseUrl);
    }

    [Fact]
    public void Resolve_UsesAnthropicTargetForMessagesProtocol()
    {
        var target = Resolve("/v1/messages", """{"model":"claude-sonnet-4-20250514","messages":[{"role":"user","content":"hello"}]}""");

        Assert.Equal("https://anthropic.example/v1", target.BaseUrl);
    }

    [Fact]
    public void Resolve_UsesGeminiTargetForGeminiBodyProtocol()
    {
        var target = Resolve("/v1beta/models/gemini-1.5-pro:generateContent", """{"contents":[{"parts":[{"text":"hello"}]}]}""");

        Assert.Equal("https://gemini.example", target.BaseUrl);
    }

    [Fact]
    public void Resolve_UsesOpenAiTargetForOpenAiModelDetailPath()
    {
        var target = Resolve("/v1/models/gpt-5.4-mini", "{}");

        Assert.Equal("https://openai.example/v1", target.BaseUrl);
    }

    [Fact]
    public void Resolve_UsesGeminiTargetForNativeGenerateContentPathWithoutBodyModel()
    {
        var target = Resolve("/v1beta/models/gemini-1.5-pro:generateContent", "{}");

        Assert.Equal("https://gemini.example", target.BaseUrl);
    }

    [Fact]
    public void Resolve_UsesGeminiTargetForGeminiModelListPath()
    {
        var target = Resolve("/v1beta/models", "{}");

        Assert.Equal("https://gemini.example", target.BaseUrl);
    }

    [Fact]
    public void Resolve_FallsBackToExplicitDefaultWhenProtocolHasNoMatchingTarget()
    {
        var config = ConfigWithTargets();
        config.Proxy.Targets.Remove("anthropic");

        var target = Resolve("/v1/messages", """{"model":"claude-sonnet-4-20250514","messages":[]}""", config);

        Assert.Equal("https://openai.example/v1", target.BaseUrl);
    }

    [Fact]
    public void Resolve_DoesNotUseModelKeyAsTargetSelector()
    {
        var config = ConfigWithTargets();
        config.Proxy.Targets["gpt-5.4-mini"] = new UpstreamTarget
        {
            BaseUrl = "https://model-key.example/v1",
            ApiKey = "model-key",
            Provider = "openai-compatible"
        };

        var target = Resolve("/v1/responses", """{"model":"gpt-5.4-mini","input":"hello"}""", config);

        Assert.Equal("https://openai.example/v1", target.BaseUrl);
    }

    [Fact]
    public void Resolve_UsesSingleTargetWhenProtocolUnknownAndNoDefaultExists()
    {
        var config = new PrivacyConfig
        {
            Proxy = new ProxyConfig
            {
                Targets = new Dictionary<string, UpstreamTarget>
                {
                    ["only"] = new()
                    {
                        BaseUrl = "https://only.example/v1",
                        ApiKey = "only-key",
                        Provider = "openai"
                    }
                }
            }
        };

        var target = Resolve("/unknown", "{}", config);

        Assert.Equal("https://only.example/v1", target.BaseUrl);
    }

    private static UpstreamTarget Resolve(string path, string body, PrivacyConfig? config = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        var request = JsonNode.Parse(body)!;
        return new UpstreamResolver().Resolve(context, request, config ?? ConfigWithTargets());
    }

    private static PrivacyConfig ConfigWithTargets() => new()
    {
        Proxy = new ProxyConfig
        {
            DefaultTarget = "openai",
            Targets = new Dictionary<string, UpstreamTarget>
            {
                ["openai"] = new()
                {
                    BaseUrl = "https://openai.example/v1",
                    ApiKey = "openai-key",
                    Provider = "openai"
                },
                ["anthropic"] = new()
                {
                    BaseUrl = "https://anthropic.example/v1",
                    ApiKey = "anthropic-key",
                    Provider = "anthropic"
                },
                ["gemini"] = new()
                {
                    BaseUrl = "https://gemini.example",
                    ApiKey = "gemini-key",
                    Provider = "gemini"
                }
            }
        }
    };
}
