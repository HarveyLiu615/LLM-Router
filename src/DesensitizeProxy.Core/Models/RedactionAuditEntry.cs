namespace DesensitizeProxy.Core.Models;

public sealed record RedactionAuditEntry(
    DateTimeOffset Timestamp,
    string Source,
    string Path,
    bool IsSystem,
    string Phase,
    string Label,
    int Count,
    string? OriginalValue,
    string RedactedValue);
