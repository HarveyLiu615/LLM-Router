using DesensitizeProxy.Core.Llm;
using DesensitizeProxy.Core.Models;
using DesensitizeProxy.Core.Abstractions;

namespace DesensitizeProxy.Core.Tests;

public sealed class HealthProbeTests
{
    [Fact]
    public async Task LocalModelHealthProbe_ReturnsDisabledWhenLocalModelDisabled()
    {
        var config = new PrivacyConfig();
        config.LocalModel.Enabled = false;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var probe = new LocalModelHealthProbe(options, new FakeExtractionClient(_ => throw new InvalidOperationException("should not call HTTP when disabled")));

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.Equal("disabled", result);
    }

    [Fact]
    public async Task LocalModelHealthProbe_ReturnsConnectedWhenExtractionSucceeds()
    {
        var config = new PrivacyConfig();
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var probe = new LocalModelHealthProbe(options, new FakeExtractionClient(_ => Task.FromResult("[]")));

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.Equal("connected", result);
    }

    [Fact]
    public async Task LocalModelHealthProbe_ReturnsErrorWhenExtractionFails()
    {
        var config = new PrivacyConfig();
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var probe = new LocalModelHealthProbe(options, new FakeExtractionClient(_ => throw new HttpRequestException("boom")));

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.Equal("error", result);
    }

    [Fact]
    public async Task LocalModelHealthProbe_ReturnsTimeoutWhenProbeTimesOut()
    {
        var config = new PrivacyConfig();
        config.LocalModel.TimeoutMs = 1;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var probe = new LocalModelHealthProbe(options, new FakeExtractionClient(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return "[]";
        }));

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.Equal("timeout", result);
    }

    private sealed class FakeExtractionClient : ILocalPiiExtractionClient
    {
        private readonly Func<CancellationToken, Task<string>> _handler;

        public FakeExtractionClient(Func<CancellationToken, Task<string>> handler)
        {
            _handler = handler;
        }

        public Task<string> ExtractAsync(string content, CancellationToken cancellationToken) => _handler(cancellationToken);
    }
}
