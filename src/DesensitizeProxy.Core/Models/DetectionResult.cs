namespace DesensitizeProxy.Core.Models;

public enum RuleHitLevel
{
    None,
    Hint,
    Trigger
}

public sealed record DetectionResult(
    RuleHitLevel HitLevel,
    string? Reason,
    double Confidence)
{
    public static DetectionResult None { get; } = new(RuleHitLevel.None, null, 0);
}
