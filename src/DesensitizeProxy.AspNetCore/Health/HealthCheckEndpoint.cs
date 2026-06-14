using DesensitizeProxy.Core.Abstractions;
using DesensitizeProxy.Core.Models;
using Microsoft.Extensions.Options;

namespace DesensitizeProxy.AspNetCore.Health;

public static class HealthCheckEndpoint
{
    public static void MapPrivacyProxyHealth(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptionsMonitor<PrivacyConfig>>().CurrentValue;
        if (!options.Observability.HealthCheckEnabled)
        {
            return;
        }

        app.MapGet(options.Observability.HealthCheckPath, async (
            IOptionsMonitor<PrivacyConfig> config,
            IDesensitizeCache cache,
            ILocalModelHealthProbe localModelHealthProbe,
            CancellationToken cancellationToken) =>
        {
            var current = config.CurrentValue;
            var llm = await localModelHealthProbe.CheckAsync(cancellationToken);
            var status = llm == "connected" ? "healthy" : "degraded";
            var body = new
            {
                status,
                regex = "ok",
                llm,
                llm_model = current.LocalModel.Model,
                llm_endpoint = current.LocalModel.Endpoint,
                cache_entries = cache.Count
            };
            return status == "healthy" ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
    }
}
