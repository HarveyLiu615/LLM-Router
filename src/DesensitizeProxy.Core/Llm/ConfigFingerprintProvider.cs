using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesensitizeProxy.Core.Models;
using Microsoft.Extensions.Options;

namespace DesensitizeProxy.Core.Llm;

public sealed class ConfigFingerprintProvider
{
    private readonly IOptionsMonitor<PrivacyConfig> _options;
    private readonly PromptLoader _promptLoader;

    public ConfigFingerprintProvider(IOptionsMonitor<PrivacyConfig> options, PromptLoader promptLoader)
    {
        _options = options;
        _promptLoader = promptLoader;
    }

    public string Current
    {
        get
        {
            var config = _options.CurrentValue;
            var payload = new
            {
                Prompt = _promptLoader.Load(),
                config.LocalModel.Model,
                MappingVersion = 1,
                config.Redaction,
                config.Keywords
            };
            var json = JsonSerializer.Serialize(payload);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }
    }
}
