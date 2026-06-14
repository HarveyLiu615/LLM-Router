using System.Diagnostics.Metrics;
using DesensitizeProxy.Core.Models;

namespace DesensitizeProxy.Core.Tests;

public sealed class DesensitizeMetricsTests
{
    [Fact]
    public void AddRegexHit_RecordsPhaseTagWhenMetricsEnabled()
    {
        var options = new TestOptionsMonitor<PrivacyConfig>(new PrivacyConfig());
        using var listener = new MeterListener();
        var measurements = new List<(long Value, string? Phase)>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "DesensitizeProxy" && instrument.Name == "regex_hits_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            measurements.Add((value, FindTag(tags, "phase")));
        });
        listener.Start();
        var metrics = new DesensitizeMetrics(options);

        metrics.AddRegexHit("phase2");
        listener.RecordObservableInstruments();

        Assert.Contains(measurements, item => item.Value == 1 && item.Phase == "phase2");
    }

    [Fact]
    public void AddLlmFailure_RecordsReasonTagWhenMetricsEnabled()
    {
        var options = new TestOptionsMonitor<PrivacyConfig>(new PrivacyConfig());
        using var listener = new MeterListener();
        var reasons = new List<string?>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "DesensitizeProxy" && instrument.Name == "llm_failures_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            reasons.Add(FindTag(tags, "reason"));
        });
        listener.Start();
        var metrics = new DesensitizeMetrics(options);

        metrics.AddLlmFailure("timeout");

        Assert.Contains("timeout", reasons);
    }

    [Fact]
    public void Metrics_DoNotRecordWhenDisabled()
    {
        var config = new PrivacyConfig();
        config.Observability.MetricsEnabled = false;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        using var listener = new MeterListener();
        var count = 0;
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "DesensitizeProxy" && instrument.Name == "desensitize_requests_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => count++);
        listener.Start();
        var metrics = new DesensitizeMetrics(options);

        metrics.AddRequest();

        Assert.Equal(0, count);
    }

    private static string? FindTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == key)
            {
                return tag.Value as string;
            }
        }

        return null;
    }
}
