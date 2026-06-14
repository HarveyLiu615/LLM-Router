using DesensitizeProxy.Core.Models;
using DesensitizeProxy.Core.Redaction;

namespace DesensitizeProxy.Core.Tests;

public sealed class PiiRedactorTests
{
    [Fact]
    public void Redact_RemovesPhase1AndPhase2AndContextSecrets()
    {
        var redactor = CreateRedactor();

        var redaction = redactor.RedactWithHits("电话13912345678 邮箱 a@example.com password: hunter2 api_key: sk-test123");
        var result = redaction.Content;

        Assert.Contains("[REDACTED:PHONE]", result);
        Assert.Contains("[REDACTED:EMAIL]", result);
        Assert.Contains("[REDACTED:PASSWORD]", result);
        Assert.Contains("[REDACTED:API_KEY]", result);
        Assert.DoesNotContain("13912345678", result);
        Assert.DoesNotContain("a@example.com", result);
        Assert.Contains("phase2", redaction.HitPhases);
        Assert.Contains("phase3", redaction.HitPhases);
        Assert.Contains(redaction.Hits, hit => hit is { Phase: "phase2", Label: "PHONE", OriginalValue: "13912345678", RedactedValue: "[REDACTED:PHONE]", Count: 1 });
        Assert.Contains(redaction.Hits, hit => hit is { Phase: "phase2", Label: "EMAIL", OriginalValue: "a@example.com", RedactedValue: "[REDACTED:EMAIL]", Count: 1 });
        Assert.Contains(redaction.Hits, hit => hit is { Phase: "phase3", Label: "PASSWORD", OriginalValue: "hunter2", RedactedValue: "[REDACTED:PASSWORD]", Count: 1 });
        Assert.Contains(redaction.Hits, hit => hit is { Phase: "phase3", Label: "API_KEY", OriginalValue: "sk-test123", RedactedValue: "[REDACTED:API_KEY]", Count: 1 });
    }

    [Fact]
    public void RedactWithHits_ReportsPhase1()
    {
        var redactor = CreateRedactor();

        var redaction = redactor.RedactWithHits("redis://user:pass@localhost:6379/0");

        Assert.Contains("[REDACTED:DB_CONNECTION]", redaction.Content);
        Assert.Contains("phase1", redaction.HitPhases);
        Assert.Contains(redaction.Hits, hit => hit is { Phase: "phase1", Label: "DB_CONNECTION", OriginalValue: "redis://user:pass@localhost:6379/0", RedactedValue: "[REDACTED:DB_CONNECTION]", Count: 1 });
    }

    [Theory]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----", "[REDACTED:PRIVATE_KEY]")]
    [InlineData("AKIA1234567890ABCDEF", "[REDACTED:AWS_KEY]")]
    [InlineData("快递单号 SF1234567890", "[REDACTED:DELIVERY]")]
    [InlineData("门禁码 1234#", "[REDACTED:ACCESS_CODE]")]
    public void RedactPhase1Only_CoversBuiltInLowFalsePositiveRules(string input, string expected)
    {
        var redactor = CreateRedactor();

        var result = redactor.RedactPhase1Only(input);

        Assert.Contains(expected, result);
    }

    [Fact]
    public void RedactSystem_DefaultRunsPhase1AndPhase2ButSkipsPhase3()
    {
        var redactor = CreateRedactor();
        var config = new SystemMessageRedactionConfig();

        var result = redactor.RedactSystem("system: 用户手机13912345678 password: keep-field", config);

        Assert.Contains("[REDACTED:PHONE]", result);
        Assert.Contains("password: keep-field", result);
    }

    [Fact]
    public void Redact_DoesNotRedactContextInsideCodeFence()
    {
        var redactor = CreateRedactor();

        var result = redactor.Redact("```\npassword: hunter2\n```\n密码是 abc123");

        Assert.Contains("password: hunter2", result);
        Assert.Contains("[REDACTED:PASSWORD]", result);
    }

    [Theory]
    [InlineData("密码 hunter2", "[REDACTED:PASSWORD]")]
    [InlineData("验证码：889900", "[REDACTED:VERIFICATION]")]
    [InlineData("身份证号是430102199001011234", "[REDACTED:ID]")]
    [InlineData("银行卡号=6222021234567890123", "[REDACTED:CARD]")]
    public void Redact_HandlesChineseLooseAndStrictKeywordRules(string input, string expected)
    {
        var redactor = CreateRedactor();

        var result = redactor.Redact(input);

        Assert.Contains(expected, result);
    }

    [Fact]
    public void RedactWithHits_ReportsOriginalValueForChineseKeywordRules()
    {
        var redactor = CreateRedactor();

        var redaction = redactor.RedactWithHits("验证码：889900");

        Assert.Contains(redaction.Hits, hit => hit is { Phase: "phase3", Label: "VERIFICATION", OriginalValue: "889900", RedactedValue: "[REDACTED:VERIFICATION]", Count: 1 });
    }

    [Fact]
    public void Redact_DoesNotUseChineseWordBoundaryLookbehind()
    {
        var redactor = CreateRedactor();

        var result = redactor.Redact("他的密码是hunter2");

        Assert.Contains("密码是[REDACTED:PASSWORD]", result);
    }

    [Fact]
    public void Redact_DoesNotTreatPasswordLoginAsPasswordValue()
    {
        var redactor = CreateRedactor();

        var redaction = redactor.RedactWithHits("点击密码登录\"按钮");

        Assert.Equal("点击密码登录\"按钮", redaction.Content);
        Assert.DoesNotContain(redaction.Hits, hit => hit.Label == "PASSWORD" && hit.OriginalValue == "登录");
    }

    [Theory]
    [InlineData("token count:")]
    [InlineData("token budget")]
    [InlineData("cancellationToken)")]
    [InlineData("password \\")]
    [InlineData("password |")]
    [InlineData("api key 检测采用")]
    [InlineData("secret_key [REDACTED_SECRET]")]
    [InlineData("身份证号是[REDACTED:ID]")]
    [InlineData("password: ...`、`api_key:")]
    [InlineData("password: hunter2`")]
    [InlineData("api_key: `、`Bearer`")]
    [InlineData("token: xxx`、中文“身份证号是...”等命中。")]
    [InlineData("Bearer `、`Bearer")]
    [InlineData("api_key: token:")]
    public void Redact_DoesNotReportKnownContextFalsePositives(string input)
    {
        var redactor = CreateRedactor();

        var redaction = redactor.RedactWithHits(input);

        Assert.Equal(input, redaction.Content);
        Assert.Empty(redaction.Hits);
    }

    [Theory]
    [InlineData("token: sk-test-token-123", "TOKEN")]
    [InlineData("Bearer eyJhbGciOiJIUzI1", "TOKEN")]
    [InlineData("password is hunter2", "PASSWORD")]
    [InlineData("api_key: sk-test123", "API_KEY")]
    [InlineData("api_key: sk-test123,", "API_KEY")]
    public void Redact_StillReportsPlausibleContextSecrets(string input, string label)
    {
        var redactor = CreateRedactor();

        var redaction = redactor.RedactWithHits(input);

        Assert.Contains(redaction.Hits, hit => hit.Label == label);
    }

    [Fact]
    public void Redact_UsesOptionalChineseAddressRuleWhenEnabled()
    {
        var config = new PrivacyConfig();
        config.Redaction.ChineseAddress = true;
        var redactor = new PiiRedactor(new TestOptionsMonitor<PrivacyConfig>(config));

        var result = redactor.Redact("地址：北京市朝阳区幸福路88号1单元101室");

        Assert.Contains("[REDACTED:ADDRESS]", result);
    }

    private static PiiRedactor CreateRedactor() =>
        new(new TestOptionsMonitor<PrivacyConfig>(new PrivacyConfig()));
}
