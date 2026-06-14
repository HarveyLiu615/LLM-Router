using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DesensitizeProxy.Core.Engine;

public sealed class DynamicRegexes
{
    private const int Limit = 500;
    private readonly ConcurrentDictionary<string, Regex> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    public Regex Get(string pattern, RegexOptions options = RegexOptions.None)
    {
        var key = (int)options + ":" + pattern;
        if (_cache.TryGetValue(key, out var regex))
        {
            return regex;
        }

        regex = new Regex(pattern, options | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        if (_cache.TryAdd(key, regex))
        {
            _order.Enqueue(key);
            Trim();
        }

        return _cache[key];
    }

    private void Trim()
    {
        while (_cache.Count > Limit && _order.TryDequeue(out var oldest))
        {
            _cache.TryRemove(oldest, out _);
        }
    }
}
