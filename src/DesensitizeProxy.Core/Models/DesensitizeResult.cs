namespace DesensitizeProxy.Core.Models;

public enum DesensitizeStatus
{
    Success,
    ModelFailure,
    ParseFailure,
    EmptyResult,
    AllItemsInvalid
}

public sealed record DesensitizeResult(
    string RedactedContent,
    bool WasModelUsed,
    DesensitizeStatus Status,
    string? FailureReason,
    IReadOnlyList<RedactionHit>? Hits = null)
{
    public bool Failed => Status != DesensitizeStatus.Success;
    public IReadOnlyList<RedactionHit> RedactionHits => Hits ?? [];
}

public sealed record PiiItem(string Type, string Value);
