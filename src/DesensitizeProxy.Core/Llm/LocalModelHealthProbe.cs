using DesensitizeProxy.Core.Abstractions;
using DesensitizeProxy.Core.Models;
using Microsoft.Extensions.Options;

namespace DesensitizeProxy.Core.Llm;

public sealed class LocalModelHealthProbe : ILocalModelHealthProbe
{
    private readonly IOptionsMonitor<PrivacyConfig> _options;
    private readonly ILocalPiiExtractionClient _client;

    public LocalModelHealthProbe(IOptionsMonitor<PrivacyConfig> options, ILocalPiiExtractionClient client)
    {
        _options = options;
        _client = client;
    }

    public async Task<string> CheckAsync(CancellationToken cancellationToken)
    {
        var config = _options.CurrentValue;
        if (!config.LocalModel.Enabled)
        {
            return "disabled";
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, config.LocalModel.TimeoutMs)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await _client.ExtractAsync("health check: no pii", linked.Token);
            return "connected";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "timeout";
        }
        catch (Exception)
        {
            return "error";
        }
    }
}
