using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace DesensitizeProxy.Core.Models;

public sealed class DesensitizeMetrics
{
    private readonly Meter _meter = new("DesensitizeProxy", "1.0.0");
    private readonly IOptionsMonitor<PrivacyConfig> _options;

    public Counter<long> RequestsTotal { get; }
    public Counter<long> RegexHitsTotal { get; }
    public Counter<long> LlmDesensitizeCallsTotal { get; }
    public Histogram<double> LlmDesensitizeDurationSeconds { get; }
    public Counter<long> LlmFailuresTotal { get; }
    public Counter<long> LlmCacheHitsTotal { get; }
    public Counter<long> StrictModeBlocksTotal { get; }

    public DesensitizeMetrics(IOptionsMonitor<PrivacyConfig>? options = null)
    {
        _options = options ?? new DisabledOptionsMonitor();
        RequestsTotal = _meter.CreateCounter<long>("desensitize_requests_total");
        RegexHitsTotal = _meter.CreateCounter<long>("regex_hits_total");
        LlmDesensitizeCallsTotal = _meter.CreateCounter<long>("llm_desensitize_calls_total");
        LlmDesensitizeDurationSeconds = _meter.CreateHistogram<double>("llm_desensitize_duration_seconds");
        LlmFailuresTotal = _meter.CreateCounter<long>("llm_failures_total");
        LlmCacheHitsTotal = _meter.CreateCounter<long>("llm_cache_hits_total");
        StrictModeBlocksTotal = _meter.CreateCounter<long>("strict_mode_blocks_total");
    }

    public void AddRequest() => Add(RequestsTotal);

    public void AddRegexHit(string phase) => Add(RegexHitsTotal, new KeyValuePair<string, object?>("phase", phase));

    public void AddLlmCall() => Add(LlmDesensitizeCallsTotal);

    public void RecordLlmDuration(double seconds)
    {
        if (Enabled)
        {
            LlmDesensitizeDurationSeconds.Record(seconds);
        }
    }

    public void AddLlmFailure(string reason) => Add(LlmFailuresTotal, new KeyValuePair<string, object?>("reason", reason));

    public void AddCacheHit() => Add(LlmCacheHitsTotal);

    public void AddStrictModeBlock() => Add(StrictModeBlocksTotal);

    private bool Enabled => _options.CurrentValue.Observability.MetricsEnabled;

    private void Add(Counter<long> counter, params KeyValuePair<string, object?>[] tags)
    {
        if (Enabled)
        {
            counter.Add(1, tags);
        }
    }

    private sealed class DisabledOptionsMonitor : IOptionsMonitor<PrivacyConfig>
    {
        public PrivacyConfig CurrentValue { get; } = new();
        public PrivacyConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PrivacyConfig, string?> listener) => null;
    }
}
