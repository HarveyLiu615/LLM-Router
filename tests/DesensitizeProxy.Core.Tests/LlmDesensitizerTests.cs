using DesensitizeProxy.Core.Abstractions;
using DesensitizeProxy.Core.Llm;
using DesensitizeProxy.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesensitizeProxy.Core.Tests;

public sealed class LlmDesensitizerTests
{
    private static readonly PiiJsonParser Parser = new();

    [Fact]
    public void BuildResult_ReturnsParseFailureForInvalidJson()
    {
        var result = LlmDesensitizer.BuildResult("张三", Trigger(), "not json", Parser);

        Assert.Equal(DesensitizeStatus.ParseFailure, result.Status);
    }

    [Fact]
    public void BuildResult_ReturnsEmptyResultForTriggerEmptyArray()
    {
        var result = LlmDesensitizer.BuildResult("张三住北京", Trigger(), "[]", Parser);

        Assert.Equal(DesensitizeStatus.EmptyResult, result.Status);
    }

    [Fact]
    public void BuildResult_ReturnsSuccessForNoneEmptyArray()
    {
        var result = LlmDesensitizer.BuildResult("解释排序算法", DetectionResult.None, "[]", Parser);

        Assert.Equal(DesensitizeStatus.Success, result.Status);
    }

    [Fact]
    public void BuildResult_ReturnsAllItemsInvalidWhenValuesAreMissing()
    {
        var result = LlmDesensitizer.BuildResult("张三住北京", Trigger(), "[{\"type\":\"NAME\",\"value\":\"李四\"}]", Parser);

        Assert.Equal(DesensitizeStatus.AllItemsInvalid, result.Status);
    }

    [Fact]
    public void BuildResult_ReplacesLongerValuesFirst()
    {
        var raw = """
        [{"type":"ADDRESS","value":"北京市朝阳区xx路xx号"},{"type":"NAME","value":"张三"},{"type":"PHONE","value":"13912345678"}]
        """;

        var result = LlmDesensitizer.BuildResult("张三住在北京市朝阳区xx路xx号，电话13912345678", Trigger(), raw, Parser);

        Assert.Equal(DesensitizeStatus.Success, result.Status);
        Assert.Equal("[REDACTED:NAME]住在[REDACTED:ADDRESS]，电话[REDACTED:PHONE]", result.RedactedContent);
        Assert.Contains(result.RedactionHits, hit => hit is { Phase: "llm", Label: "NAME", OriginalValue: "张三", RedactedValue: "[REDACTED:NAME]", Count: 1 });
        Assert.Contains(result.RedactionHits, hit => hit is { Phase: "llm", Label: "ADDRESS", OriginalValue: "北京市朝阳区xx路xx号", RedactedValue: "[REDACTED:ADDRESS]", Count: 1 });
        Assert.Contains(result.RedactionHits, hit => hit is { Phase: "llm", Label: "PHONE", OriginalValue: "13912345678", RedactedValue: "[REDACTED:PHONE]", Count: 1 });
    }

    [Fact]
    public async Task DesensitizeAsync_ReturnsModelFailureOnTimeout()
    {
        var config = new PrivacyConfig();
        config.LocalModel.TimeoutMs = 1;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var desensitizer = CreateDesensitizer(options, new FakeExtractionClient(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return "[]";
        }));

        var result = await desensitizer.DesensitizeAsync("张三", Trigger(), CancellationToken.None);

        Assert.Equal(DesensitizeStatus.ModelFailure, result.Status);
        Assert.Equal("timeout", result.FailureReason);
    }

    [Fact]
    public async Task DesensitizeAsync_CachesOnlySuccessfulResults()
    {
        var options = new TestOptionsMonitor<PrivacyConfig>(new PrivacyConfig());
        var calls = 0;
        var desensitizer = CreateDesensitizer(options, new FakeExtractionClient(_ =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? "not json"
                : "[{\"type\":\"NAME\",\"value\":\"张三\"}]");
        }));

        var first = await desensitizer.DesensitizeAsync("张三", Trigger(), CancellationToken.None);
        var second = await desensitizer.DesensitizeAsync("张三", Trigger(), CancellationToken.None);
        var third = await desensitizer.DesensitizeAsync("张三", Trigger(), CancellationToken.None);

        Assert.Equal(DesensitizeStatus.ParseFailure, first.Status);
        Assert.Equal(DesensitizeStatus.Success, second.Status);
        Assert.Equal(DesensitizeStatus.Success, third.Status);
        Assert.False(third.WasModelUsed);
        Assert.Contains(third.RedactionHits, hit => hit is { Phase: "llm-cache", Label: "NAME", OriginalValue: "张三", RedactedValue: "[REDACTED:NAME]", Count: 1 });
        Assert.Equal(2, calls);
    }

    private static DetectionResult Trigger() => new(RuleHitLevel.Trigger, "test", 1.0);

    private static LlmDesensitizer CreateDesensitizer(
        TestOptionsMonitor<PrivacyConfig> options,
        ILocalPiiExtractionClient client)
    {
        var promptLoader = new PromptLoader(new TestHostEnvironment(), options);
        return new LlmDesensitizer(
            client,
            new PiiJsonParser(),
            new DesensitizeCache(),
            new ConfigFingerprintProvider(options, promptLoader),
            options,
            NullLogger<LlmDesensitizer>.Instance);
    }

    private sealed class FakeExtractionClient : ILocalPiiExtractionClient
    {
        private readonly Func<CancellationToken, Task<string>> _handler;

        public FakeExtractionClient(Func<CancellationToken, Task<string>> handler)
        {
            _handler = handler;
        }

        public Task<string> ExtractAsync(string content, CancellationToken cancellationToken) => _handler(cancellationToken);
    }
}
