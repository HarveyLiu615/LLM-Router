using System.Net;
using DesensitizeProxy.AspNetCore.Yarp;
using DesensitizeProxy.Core.Llm;
using DesensitizeProxy.Core.Models;
using DesensitizeProxy.Core.Redaction;

namespace DesensitizeProxy.Core.Tests;

public sealed class IntegrationTests
{
    [Fact]
    public void LlmResultThenRegexFallback_DoesNotRestoreRegexDetectedPii()
    {
        var options = new TestOptionsMonitor<PrivacyConfig>(new PrivacyConfig());
        var redactor = new PiiRedactor(options);
        var raw = "[{\"type\":\"NAME\",\"value\":\"张三\"}]";

        var llm = LlmDesensitizer.BuildResult(
            "张三 电话13912345678",
            new DetectionResult(RuleHitLevel.Trigger, "test", 1),
            raw,
            new PiiJsonParser());
        var final = redactor.Redact(llm.RedactedContent);

        Assert.Equal("[REDACTED:NAME] 电话[REDACTED:PHONE]", final);
    }

    [Fact]
    public void NativeProviderResponse_IsMarkedAsProviderNative()
    {
        var transformer = new AnthropicTransformer();
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        transformer.ApplyResponseHeaders(response, new UpstreamTarget
        {
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "test",
            Provider = "anthropic"
        });

        Assert.True(response.Headers.TryGetValues("x-desensitize-response-mode", out var values));
        Assert.Contains("provider-native", values);
    }
}
