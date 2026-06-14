using DesensitizeProxy.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace DesensitizeProxy.Core.Runtime;

public sealed class RuntimeDirectoryResolver
{
    private readonly IHostEnvironment _environment;
    private readonly IOptionsMonitor<PrivacyConfig> _options;

    public RuntimeDirectoryResolver(IHostEnvironment environment, IOptionsMonitor<PrivacyConfig> options)
    {
        _environment = environment;
        _options = options;
    }

    public string DataDirectory => Resolve(_options.CurrentValue.Runtime.DataDirectory, DefaultDataDirectory());
    public string LogDirectory => Resolve(_options.CurrentValue.Runtime.LogDirectory, AppContext.BaseDirectory);

    private string Resolve(string? configured, string fallback)
    {
        var path = string.IsNullOrWhiteSpace(configured) ? fallback : configured;
        path = ExpandEnvironmentPlaceholders(path);
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(_environment.ContentRootPath, path);
        }

        return Path.GetFullPath(path);
    }

    private static string ExpandEnvironmentPlaceholders(string path)
    {
        var expanded = Regex.Replace(path, "\\$\\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\\}", match =>
        {
            var name = match.Groups["name"].Value;
            return Environment.GetEnvironmentVariable(name) ?? string.Empty;
        });
        return Environment.ExpandEnvironmentVariables(expanded);
    }

    private static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ClawXRouter", "PrivacyProxy");
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawXRouter", "PrivacyProxy");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share") : xdg, "clawxrouter", "privacy-proxy");
    }

}
