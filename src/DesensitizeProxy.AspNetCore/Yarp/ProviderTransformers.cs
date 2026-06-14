using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using DesensitizeProxy.Core.Models;

namespace DesensitizeProxy.AspNetCore.Yarp;

public interface IProviderTransformer
{
    string BuildPath(PathString originalPath, QueryString originalQuery, JsonNode? request, UpstreamTarget target);
    JsonNode TransformRequest(JsonNode request, UpstreamTarget target);
    void ApplyResponseHeaders(HttpResponseMessage upstreamResponse, UpstreamTarget target);
}

public sealed class ProviderTransformerRegistry
{
    private readonly Dictionary<string, IProviderTransformer> _transformers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = new OpenAiCompatibleTransformer(),
        ["openai-compatible"] = new OpenAiCompatibleTransformer(),
        ["anthropic"] = new AnthropicTransformer(),
        ["google"] = new GeminiTransformer(),
        ["gemini"] = new GeminiTransformer(),
        ["vertex"] = new GeminiTransformer()
    };

    public IProviderTransformer Resolve(UpstreamTarget target)
    {
        var provider = target.Provider ?? "openai-compatible";
        if (!_transformers.TryGetValue(provider, out var transformer))
        {
            throw new NotSupportedException($"Provider '{provider}' is not supported");
        }

        return transformer;
    }
}

public sealed class OpenAiCompatibleTransformer : IProviderTransformer
{
    public string BuildPath(PathString originalPath, QueryString originalQuery, JsonNode? request, UpstreamTarget target) =>
        NormalizePath(originalPath, target) + originalQuery.ToUriComponent();

    public JsonNode TransformRequest(JsonNode request, UpstreamTarget target) => request.DeepClone();

    public void ApplyResponseHeaders(HttpResponseMessage upstreamResponse, UpstreamTarget target)
    {
    }

    private static string NormalizePath(PathString originalPath, UpstreamTarget target)
    {
        var basePath = new Uri(target.BaseUrl.TrimEnd('/') + "/").AbsolutePath.TrimEnd('/');
        var path = originalPath.Value ?? "/";
        if (!string.IsNullOrEmpty(basePath) && path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            return path[basePath.Length..];
        }

        return path;
    }
}

public sealed class AnthropicTransformer : IProviderTransformer
{
    public string BuildPath(PathString originalPath, QueryString originalQuery, JsonNode? request, UpstreamTarget target)
    {
        var path = originalPath.Value ?? string.Empty;
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return "/v1/messages" + originalQuery.ToUriComponent();
        }

        return path + originalQuery.ToUriComponent();
    }

    public JsonNode TransformRequest(JsonNode request, UpstreamTarget target)
    {
        var obj = request.AsObject();
        var transformed = new JsonObject
        {
            ["model"] = obj["model"]?.DeepClone(),
            ["max_tokens"] = obj["max_tokens"]?.DeepClone() ?? 4096,
            ["stream"] = obj["stream"]?.DeepClone() ?? false
        };

        if (obj["messages"] is JsonArray messages)
        {
            var outputMessages = new JsonArray();
            var system = new StringBuilder();
            foreach (var node in messages)
            {
                if (node is not JsonObject message)
                {
                    continue;
                }

                var role = message["role"]?.GetValue<string>();
                if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase))
                {
                    AppendSystem(system, message["content"]);
                    continue;
                }

                outputMessages.Add(message.DeepClone());
            }

            transformed["messages"] = outputMessages;
            if (system.Length > 0)
            {
                transformed["system"] = system.ToString();
            }
        }

        if (obj["tools"] is JsonNode tools)
        {
            transformed["tools"] = TransformTools(tools);
        }

        return transformed;
    }

    public void ApplyResponseHeaders(HttpResponseMessage upstreamResponse, UpstreamTarget target)
    {
        upstreamResponse.Headers.TryAddWithoutValidation("x-desensitize-provider", "anthropic");
        upstreamResponse.Headers.TryAddWithoutValidation("x-desensitize-response-mode", "provider-native");
    }

    private static void AppendSystem(StringBuilder builder, JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(text);
        }
    }

    private static JsonArray TransformTools(JsonNode tools)
    {
        var output = new JsonArray();
        if (tools is not JsonArray array)
        {
            return output;
        }

        foreach (var node in array.OfType<JsonObject>())
        {
            var function = node["function"] as JsonObject;
            if (function is null)
            {
                continue;
            }

            var tool = new JsonObject
            {
                ["name"] = function["name"]?.DeepClone(),
                ["description"] = function["description"]?.DeepClone(),
                ["input_schema"] = function["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object" }
            };
            output.Add(tool);
        }

        return output;
    }
}

public sealed class GeminiTransformer : IProviderTransformer
{
    public string BuildPath(PathString originalPath, QueryString originalQuery, JsonNode? request, UpstreamTarget target)
    {
        var path = originalPath.Value ?? string.Empty;
        if (path.Equals("/v1beta/models", StringComparison.OrdinalIgnoreCase))
        {
            return path + originalQuery.ToUriComponent();
        }

        var model = ExtractModelFromPath(path) ?? ExtractModelFromRequest(request);
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("Gemini/Vertex native upstream requires request body field 'model'.");
        }

        var suffix = path.Contains("stream", StringComparison.OrdinalIgnoreCase) ? ":streamGenerateContent" : ":generateContent";
        return $"/v1beta/{model}{suffix}{originalQuery.ToUriComponent()}";
    }

    public JsonNode TransformRequest(JsonNode request, UpstreamTarget target)
    {
        if (request["contents"] is not null)
        {
            return request.DeepClone();
        }

        var obj = request.AsObject();
        var contents = new JsonArray();
        var systemParts = new JsonArray();
        if (obj["messages"] is JsonArray messages)
        {
            foreach (var node in messages)
            {
                if (node is not JsonObject message)
                {
                    continue;
                }

                var role = message["role"]?.GetValue<string>() ?? "user";
                var text = ExtractText(message["content"]);
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                if (role.Equals("system", StringComparison.OrdinalIgnoreCase) ||
                    role.Equals("developer", StringComparison.OrdinalIgnoreCase))
                {
                    systemParts.Add(new JsonObject { ["text"] = text });
                    continue;
                }

                contents.Add(new JsonObject
                {
                    ["role"] = role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = text })
                });
            }
        }

        var transformed = new JsonObject { ["contents"] = contents };
        if (systemParts.Count > 0)
        {
            transformed["systemInstruction"] = new JsonObject { ["parts"] = systemParts };
        }

        if (obj["tools"] is JsonNode tools)
        {
            var declarations = TransformTools(tools);
            if (declarations.Count > 0)
            {
                transformed["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
            }
        }

        return transformed;
    }

    public void ApplyResponseHeaders(HttpResponseMessage upstreamResponse, UpstreamTarget target)
    {
        upstreamResponse.Headers.TryAddWithoutValidation("x-desensitize-provider", target.Provider ?? "gemini");
        upstreamResponse.Headers.TryAddWithoutValidation("x-desensitize-response-mode", "provider-native");
    }

    private static string? ExtractModelFromPath(string path)
    {
        var marker = "/models/";
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var model = path[(index + 1)..];
        var colon = model.IndexOf(':');
        return colon >= 0 ? model[..colon] : model;
    }

    private static string? ExtractModelFromRequest(JsonNode? request)
    {
        var model = request?["model"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return model.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? model : "models/" + model;
    }

    private static string ExtractText(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (content is JsonArray array)
        {
            return string.Join("\n", array.OfType<JsonObject>()
                .Where(part => string.Equals(part["type"]?.GetValue<string>(), "text", StringComparison.OrdinalIgnoreCase))
                .Select(part => part["text"]?.GetValue<string>())
                .Where(text => !string.IsNullOrEmpty(text)));
        }

        return string.Empty;
    }

    private static JsonArray TransformTools(JsonNode tools)
    {
        var declarations = new JsonArray();
        if (tools is not JsonArray array)
        {
            return declarations;
        }

        foreach (var node in array.OfType<JsonObject>())
        {
            var function = node["function"] as JsonObject;
            if (function is null)
            {
                continue;
            }

            declarations.Add(new JsonObject
            {
                ["name"] = function["name"]?.DeepClone(),
                ["description"] = function["description"]?.DeepClone(),
                ["parameters"] = function["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object" }
            });
        }

        return declarations;
    }
}
