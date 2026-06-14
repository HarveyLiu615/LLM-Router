using DesensitizeProxy.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DesensitizeProxy.Core.Llm;

public sealed class PromptLoader
{
    private const string DefaultPrompt = """
You extract personally identifiable information from user text.
Return only a JSON array. Each item must be {"type":"TYPE","value":"exact substring"}.
Types include NAME, PHONE, ADDRESS, ACCESS_CODE, DELIVERY, ID, CARD, LICENSE_PLATE, EMAIL, PASSWORD, PAYMENT, BIRTHDAY, NOTE, API_KEY, TOKEN, SECRET.
The value must be an exact substring from the input. Do not rewrite or infer values.
If no PII is present, return [].
""";

    private readonly IHostEnvironment _environment;
    private readonly IOptionsMonitor<PrivacyConfig> _options;
    private string? _cachedPrompt;

    public PromptLoader(IHostEnvironment environment, IOptionsMonitor<PrivacyConfig> options)
    {
        _environment = environment;
        _options = options;
        _options.OnChange(_ => _cachedPrompt = null);
    }

    public string Load()
    {
        if (_cachedPrompt is not null)
        {
            return _cachedPrompt;
        }

        var configured = _options.CurrentValue.PromptPath;
        if (!string.IsNullOrWhiteSpace(configured) && TryRead(configured, out var prompt))
        {
            return _cachedPrompt = prompt;
        }

        var contentRootPrompt = Path.Combine(_environment.ContentRootPath, "prompts", "pii-extraction.md");
        if (TryRead(contentRootPrompt, out prompt))
        {
            return _cachedPrompt = prompt;
        }

        return _cachedPrompt = DefaultPrompt;
    }

    private static bool TryRead(string path, out string content)
    {
        content = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (!File.Exists(fullPath))
            {
                return false;
            }

            content = File.ReadAllText(fullPath);
            return !string.IsNullOrWhiteSpace(content);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
