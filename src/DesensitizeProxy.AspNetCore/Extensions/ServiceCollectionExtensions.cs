using DesensitizeProxy.AspNetCore.Middleware;
using DesensitizeProxy.AspNetCore.Yarp;
using DesensitizeProxy.Core.Extensions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yarp.ReverseProxy.Forwarder;

namespace DesensitizeProxy.AspNetCore.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDesensitizeProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDesensitizeProxyCore(configuration);
        services.AddHttpForwarder();
        services.TryAddSingleton(new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false
        }));
        services.AddSingleton<UpstreamResolver>();
        services.AddSingleton<ProviderTransformerRegistry>();
        services.AddScoped<DesensitizeProxyMiddleware>();
        return services;
    }
}
