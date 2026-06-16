using DesensitizeProxy.AspNetCore.Auth;
using DesensitizeProxy.Core.Models;

namespace DesensitizeProxy.Core.Tests;

public sealed class MultiAuthHandlerTests
{
    [Fact]
    public void ResolveAuthHeaders_UsesBearerForOpenAiCompatibleProviders()
    {
        var headers = MultiAuthHandler.ResolveAuthHeaders(new UpstreamTarget
        {
            BaseUrl = "https://example.com/v1",
            ApiKey = "key",
            Provider = "openai-compatible"
        });

        Assert.Equal("Bearer key", headers["Authorization"]);
    }

    [Fact]
    public void ResolveAuthHeaders_UsesBearerForDeepSeek()
    {
        var headers = MultiAuthHandler.ResolveAuthHeaders(new UpstreamTarget
        {
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKey = "key",
            Provider = "deepseek"
        });

        Assert.Equal("Bearer key", headers["Authorization"]);
    }

    [Fact]
    public void ResolveAuthHeaders_UsesAnthropicHeaders()
    {
        var headers = MultiAuthHandler.ResolveAuthHeaders(new UpstreamTarget
        {
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "key",
            Provider = "anthropic"
        });

        Assert.Equal("key", headers["x-api-key"]);
        Assert.Equal("2023-06-01", headers["anthropic-version"]);
    }

    [Theory]
    [InlineData("google")]
    [InlineData("gemini")]
    [InlineData("vertex")]
    public void ResolveAuthHeaders_UsesGoogleApiKeyForGeminiAndVertex(string provider)
    {
        var headers = MultiAuthHandler.ResolveAuthHeaders(new UpstreamTarget
        {
            BaseUrl = "https://generativelanguage.googleapis.com",
            ApiKey = "key",
            Provider = provider
        });

        Assert.Equal("key", headers["x-goog-api-key"]);
    }

    [Fact]
    public void ResolveAuthHeaders_ExpandsBracedEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("PRIVACY_PROXY_TEST_KEY", "env-key");

        var headers = MultiAuthHandler.ResolveAuthHeaders(new UpstreamTarget
        {
            BaseUrl = "https://example.com/v1",
            ApiKey = "${PRIVACY_PROXY_TEST_KEY}",
            Provider = "openai"
        });

        Assert.Equal("Bearer env-key", headers["Authorization"]);
    }
}
