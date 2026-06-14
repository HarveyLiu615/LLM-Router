using DesensitizeProxy.Core.Abstractions;
using DesensitizeProxy.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace DesensitizeProxy.Core.Llm;

public sealed class LlmDesensitizer : ILlmDesensitizer
{
    private readonly ILocalPiiExtractionClient _client;
    private readonly PiiJsonParser _parser;
    private readonly IDesensitizeCache _cache;
    private readonly ConfigFingerprintProvider _fingerprintProvider;
    private readonly IOptionsMonitor<PrivacyConfig> _options;
    private readonly ILogger<LlmDesensitizer> _logger;

    public LlmDesensitizer(
        ILocalPiiExtractionClient client,
        PiiJsonParser parser,
        IDesensitizeCache cache,
        ConfigFingerprintProvider fingerprintProvider,
        IOptionsMonitor<PrivacyConfig> options,
        ILogger<LlmDesensitizer> logger)
    {
        _client = client;
        _parser = parser;
        _cache = cache;
        _fingerprintProvider = fingerprintProvider;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DesensitizeResult>> DesensitizeBatchAsync(
        IReadOnlyList<(string Content, DetectionResult Detection)> items,
        CancellationToken cancellationToken)
    {
        var maxConcurrency = Math.Max(1, _options.CurrentValue.LocalModel.MaxConcurrency);
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = items.Select(item => ProcessOneAsync(item.Content, item.Detection, semaphore, cancellationToken)).ToArray();
        return await Task.WhenAll(tasks);
    }

    public Task<DesensitizeResult> DesensitizeAsync(
        string content,
        DetectionResult detection,
        CancellationToken cancellationToken) =>
        ProcessOneAsync(content, detection, new SemaphoreSlim(1), cancellationToken);

    private async Task<DesensitizeResult> ProcessOneAsync(
        string content,
        DetectionResult detection,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        var fingerprint = _fingerprintProvider.Current;
        var cached = _cache.Get(content, fingerprint);
        if (cached is not null)
        {
            var cachedHits = cached.Hits
                .Select(hit => hit with { Phase = "llm-cache" })
                .ToList();
            return new DesensitizeResult(cached.RedactedContent, WasModelUsed: false, DesensitizeStatus.Success, null, cachedHits);
        }

        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var timeoutMs = Math.Max(1, _options.CurrentValue.LocalModel.TimeoutMs);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var raw = await _client.ExtractAsync(content, linked.Token);
            var result = BuildResult(content, detection, raw, _parser);
            if (result.Status == DesensitizeStatus.Success)
            {
                _cache.Set(content, new DesensitizeCacheEntry(result.RedactedContent, result.RedactionHits), fingerprint);
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("LLM desensitize timeout for {Length} chars", content.Length);
            return new DesensitizeResult(content, WasModelUsed: false, DesensitizeStatus.ModelFailure, "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "LLM desensitize failed for {Length} chars", content.Length);
            return new DesensitizeResult(content, WasModelUsed: false, DesensitizeStatus.ModelFailure, ex.Message);
        }
        finally
        {
            semaphore.Release();
        }
    }

    internal static DesensitizeResult BuildResult(
        string content,
        DetectionResult detection,
        string raw,
        PiiJsonParser parser)
    {
        var items = parser.Parse(raw);
        if (items is null)
        {
            return new DesensitizeResult(content, WasModelUsed: true, DesensitizeStatus.ParseFailure, "invalid json array");
        }

        if (items.Count == 0)
        {
            var status = detection.HitLevel == RuleHitLevel.None ? DesensitizeStatus.Success : DesensitizeStatus.EmptyResult;
            return new DesensitizeResult(content, WasModelUsed: true, status, status == DesensitizeStatus.Success ? null : "empty pii array");
        }

        var validItems = items
            .Where(item => content.Contains(item.Value, StringComparison.Ordinal))
            .OrderByDescending(item => item.Value.Length)
            .ToList();

        if (validItems.Count == 0)
        {
            return new DesensitizeResult(content, WasModelUsed: true, DesensitizeStatus.AllItemsInvalid, "all values missing from original content");
        }

        var redacted = content;
        var hits = new List<RedactionHit>();
        foreach (var item in validItems)
        {
            var count = CountOccurrences(redacted, item.Value);
            if (count == 0)
            {
                continue;
            }

            var label = PiiTypeMapper.ToRedactionLabel(item.Type);
            redacted = redacted.Replace(item.Value, label, StringComparison.Ordinal);
            hits.Add(new RedactionHit("llm", PiiTypeMapper.ToRedactionLabelName(item.Type), item.Value, label, count));
        }

        return new DesensitizeResult(redacted, WasModelUsed: true, DesensitizeStatus.Success, null, hits);
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while (index < content.Length)
        {
            var next = content.IndexOf(value, index, StringComparison.Ordinal);
            if (next < 0)
            {
                return count;
            }

            count++;
            index = next + value.Length;
        }

        return count;
    }
}
