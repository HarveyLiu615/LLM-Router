using DesensitizeProxy.Core.Abstractions;
using DesensitizeProxy.Core.Engine;
using DesensitizeProxy.Core.Llm;
using DesensitizeProxy.Core.Models;
using DesensitizeProxy.Core.Observability;
using DesensitizeProxy.Core.Redaction;
using DesensitizeProxy.Core.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DesensitizeProxy.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDesensitizeProxyCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PrivacyConfig>(configuration.GetSection(PrivacyConfig.SectionName));
        services.AddSingleton<IPiiRedactor, PiiRedactor>();
        services.AddSingleton<DynamicRegexes>();
        services.AddSingleton<IRuleEngine, RuleEngine>();
        services.AddSingleton<ISchemaCleaner, SchemaCleaner>();
        services.AddSingleton<IDesensitizeCache, DesensitizeCache>();
        services.AddSingleton<PiiJsonParser>();
        services.AddSingleton<PromptLoader>();
        services.AddSingleton<ConfigFingerprintProvider>();
        services.AddSingleton<RuntimeDirectoryResolver>();
        services.AddSingleton<DesensitizeMetrics>();
        services.AddSingleton<IRedactionAuditLogger, FileRedactionAuditLogger>();
        services.AddHttpClient<LocalModelClient>();
        services.AddScoped<ILocalPiiExtractionClient>(sp => sp.GetRequiredService<LocalModelClient>());
        services.AddScoped<ILocalModelHealthProbe, LocalModelHealthProbe>();
        services.AddScoped<ILlmDesensitizer, LlmDesensitizer>();
        return services;
    }
}
