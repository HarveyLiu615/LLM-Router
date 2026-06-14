namespace DesensitizeProxy.Core.Models;

public sealed record RedactionResult(
    string Content,
    IReadOnlySet<string> HitPhases,
    IReadOnlyList<RedactionHit> Hits)
{
    public bool HasHit => HitPhases.Count > 0;
}

public sealed record RedactionHit(
    string Phase,
    string Label,
    string? OriginalValue,
    string RedactedValue,
    int Count);
