using DesensitizeProxy.AspNetCore.Extensions;
using DesensitizeProxy.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DesensitizeProxy.Core.Tests;

public sealed class ServiceRegistrationTests
{
    [Fact]
    public void AddDesensitizeProxy_ServiceGraphBuildsWithScopeValidation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PrivacyProxy:LocalModel:Enabled"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnvironment());

        services.AddDesensitizeProxy(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ILocalModelHealthProbe>());
    }
}
