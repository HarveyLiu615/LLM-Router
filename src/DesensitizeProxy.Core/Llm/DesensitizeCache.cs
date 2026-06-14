using System.Security.Cryptography;
using System.Text;
using DesensitizeProxy.Core.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace DesensitizeProxy.Core.Llm;

public sealed class DesensitizeCache : IDesensitizeCache
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = 500,
        ExpirationScanFrequency = TimeSpan.FromMinutes(5)
    });

    private int _count;

    public int Count => Volatile.Read(ref _count);

    public DesensitizeCacheEntry? Get(string originalContent, string configFingerprint)
    {
        var key = ComputeSha256(configFingerprint + "\n" + originalContent);
        return _cache.TryGetValue(key, out DesensitizeCacheEntry? cached) ? cached : null;
    }

    public void Set(string originalContent, DesensitizeCacheEntry cacheEntry, string configFingerprint)
    {
        var key = ComputeSha256(configFingerprint + "\n" + originalContent);
        using var entry = _cache.CreateEntry(key);
        entry.Value = cacheEntry;
        entry.Size = 1;
        entry.SlidingExpiration = TimeSpan.FromMinutes(30);
        entry.RegisterPostEvictionCallback((_, _, _, _) => Interlocked.Decrement(ref _count));
        Interlocked.Increment(ref _count);
    }

    public void Clear()
    {
        _cache.Clear();
        Volatile.Write(ref _count, 0);
    }

    private static string ComputeSha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
