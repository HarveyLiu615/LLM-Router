namespace DesensitizeProxy.Core.Abstractions;

public interface IDesensitizeCache
{
    DesensitizeCacheEntry? Get(string originalContent, string configFingerprint);
    void Set(string originalContent, DesensitizeCacheEntry cacheEntry, string configFingerprint);
    int Count { get; }
    void Clear();
}

public sealed record DesensitizeCacheEntry(
    string RedactedContent,
    IReadOnlyList<Models.RedactionHit> Hits);
