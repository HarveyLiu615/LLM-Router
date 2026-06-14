using DesensitizeProxy.Core.Engine;
using DesensitizeProxy.Core.Models;
using DesensitizeProxy.Core.Redaction;

namespace DesensitizeProxy.Core.Tests;

public sealed class RuleEngineTests
{
    [Fact]
    public void Check_ReturnsTriggerForConfiguredKeyword()
    {
        var engine = CreateEngine();

        var result = engine.Check("我的身份证号是 430102199001011234");

        Assert.Equal(RuleHitLevel.Trigger, result.HitLevel);
    }

    [Fact]
    public void Check_ReturnsHintForWeakKeywordWithoutRegexHit()
    {
        var engine = CreateEngine();

        var result = engine.Check("帮我解释一下身份证校验算法");

        Assert.Equal(RuleHitLevel.Hint, result.HitLevel);
    }

    [Fact]
    public void Check_ReturnsNoneForUnrelatedText()
    {
        var engine = CreateEngine();

        var result = engine.Check("帮我写一个排序算法");

        Assert.Equal(RuleHitLevel.None, result.HitLevel);
    }

    private static RuleEngine CreateEngine()
    {
        var options = new TestOptionsMonitor<PrivacyConfig>(new PrivacyConfig());
        return new RuleEngine(new PiiRedactor(options), options);
    }
}
