using DesensitizeProxy.Core.Abstractions;
using DesensitizeProxy.Core.Models;
using Microsoft.Extensions.Options;

namespace DesensitizeProxy.Core.Engine;

public sealed class RuleEngine : IRuleEngine
{
    private readonly IPiiRedactor _redactor;
    private readonly IOptionsMonitor<PrivacyConfig> _options;

    public RuleEngine(IPiiRedactor redactor, IOptionsMonitor<PrivacyConfig> options)
    {
        _redactor = redactor;
        _options = options;
    }

    public DetectionResult Check(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return DetectionResult.None;
        }

        foreach (var keyword in _options.CurrentValue.Keywords.TriggerKeywords)
        {
            if (Contains(content, keyword))
            {
                return new DetectionResult(RuleHitLevel.Trigger, $"trigger keyword: {keyword}", 1.0);
            }
        }

        if (_redactor.HasAnyHit(content))
        {
            return new DetectionResult(RuleHitLevel.Trigger, "regex hit", 1.0);
        }

        foreach (var keyword in _options.CurrentValue.Keywords.HintKeywords)
        {
            if (Contains(content, keyword))
            {
                return new DetectionResult(RuleHitLevel.Hint, $"hint keyword: {keyword}", 0.5);
            }
        }

        return DetectionResult.None;
    }

    private static bool Contains(string content, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return false;
        }

        return content.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}
