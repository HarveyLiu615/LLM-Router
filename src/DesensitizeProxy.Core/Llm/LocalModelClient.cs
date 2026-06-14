using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DesensitizeProxy.Core.Abstractions;
using DesensitizeProxy.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DesensitizeProxy.Core.Llm;

public sealed class LocalModelClient : ILocalPiiExtractionClient
{
    private readonly HttpClient _httpClient;
    private readonly PromptLoader _promptLoader;
    private readonly IOptionsMonitor<PrivacyConfig> _options;
    private readonly ILogger<LocalModelClient> _logger;

    public LocalModelClient(
        HttpClient httpClient,
        PromptLoader promptLoader,
        IOptionsMonitor<PrivacyConfig> options,
        ILogger<LocalModelClient> logger)
    {
        _httpClient = httpClient;
        _promptLoader = promptLoader;
        _options = options;
        _logger = logger;
    }

    public async Task<string> ExtractAsync(string content, CancellationToken cancellationToken)
    {
        var config = _options.CurrentValue.LocalModel;
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatUrl(config));
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        var body = BuildRequestBody(config, content);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Local model returned {StatusCode}: {Payload}", response.StatusCode, payload);
            throw new HttpRequestException($"local model returned {(int)response.StatusCode}");
        }

        return ExtractContent(payload) ?? payload;
    }

    private object BuildRequestBody(LocalModelConfig config, string content)
    {
        var messages = new object[]
        {
            new { role = "system", content = _promptLoader.Load() },
            new { role = "user", content }
        };

        if (config.Type.Equals("ollama-native", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                model = config.Model,
                messages,
                stream = false,
                options = new { temperature = 0 }
            };
        }

        return new
        {
            model = config.Model,
            messages,
            temperature = 0,
            stream = false
        };
    }

    private static string BuildChatUrl(LocalModelConfig config)
    {
        var endpoint = config.Endpoint;
        var trimmed = endpoint.TrimEnd('/');
        if (config.Type.Equals("ollama-native", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : trimmed + "/api/chat";
        }

        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed + "/chat/completions"
            : trimmed + "/v1/chat/completions";
    }

    private static string? ExtractContent(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            var first = choices.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object &&
                first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }

        if (doc.RootElement.TryGetProperty("message", out var ollamaMessage) &&
            ollamaMessage.TryGetProperty("content", out var ollamaContent) &&
            ollamaContent.ValueKind == JsonValueKind.String)
        {
            return ollamaContent.GetString();
        }

        return null;
    }
}
