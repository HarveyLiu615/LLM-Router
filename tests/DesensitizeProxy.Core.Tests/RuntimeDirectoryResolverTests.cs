using DesensitizeProxy.Core.Models;
using DesensitizeProxy.Core.Runtime;

namespace DesensitizeProxy.Core.Tests;

public sealed class RuntimeDirectoryResolverTests
{
    [Fact]
    public void LogDirectory_DefaultsToAppBaseDirectoryWhenNotConfigured()
    {
        var resolver = new RuntimeDirectoryResolver(
            new TestHostEnvironment { ContentRootPath = Path.Combine(Path.GetTempPath(), "privacy-proxy-root") },
            new TestOptionsMonitor<PrivacyConfig>(new PrivacyConfig()));

        Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory), resolver.LogDirectory);
    }

    [Fact]
    public void DataDirectory_ResolvesRelativePathAgainstContentRoot()
    {
        var config = new PrivacyConfig();
        config.Runtime.DataDirectory = "data";
        config.Runtime.LogDirectory = "logs";
        var environment = new TestHostEnvironment { ContentRootPath = Path.GetTempPath() };
        var resolver = new RuntimeDirectoryResolver(environment, new TestOptionsMonitor<PrivacyConfig>(config));

        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "data")), resolver.DataDirectory);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "logs")), resolver.LogDirectory);
    }

    [Fact]
    public void DataDirectory_ExpandsEnvironmentVariables()
    {
        var temp = Path.Combine(Path.GetTempPath(), "privacy-proxy-test");
        Environment.SetEnvironmentVariable("PRIVACY_PROXY_TEST_DIR", temp);
        var config = new PrivacyConfig();
        config.Runtime.DataDirectory = "${PRIVACY_PROXY_TEST_DIR}";
        config.Runtime.LogDirectory = "${PRIVACY_PROXY_TEST_DIR}";
        var resolver = new RuntimeDirectoryResolver(new TestHostEnvironment(), new TestOptionsMonitor<PrivacyConfig>(config));

        Assert.Equal(Path.GetFullPath(temp), resolver.DataDirectory);
        Assert.Equal(Path.GetFullPath(temp), resolver.LogDirectory);
    }
}
