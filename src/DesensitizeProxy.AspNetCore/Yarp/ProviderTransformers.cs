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
        ["deepseek"] = new DeepSeekTransformer(),
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

    internal static string NormalizePath(PathString originalPath, UpstreamTarget target)
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

public sealed class DeepSeekTransformer : IProviderTransformer
{
    public string BuildPath(PathString originalPath, QueryString originalQuery, JsonNode? request, UpstreamTarget target)
    {
        var normalized = OpenAiCompatibleTransformer.NormalizePath(originalPath, target);
        if (normalized.Contains("/responses", StringComparison.OrdinalIgnoreCase))
        {
            return "/chat/completions" + originalQuery.ToUriComponent();
        }

        return normalized + originalQuery.ToUriComponent();
    }

    public JsonNode TransformRequest(JsonNode request, UpstreamTarget target)
    {
        var obj = request.AsObject();
        if (obj["input"] is null && obj["instructions"] is null && obj["reasoning"] is null && obj["max_output_tokens"] is null)
        {
            return request.DeepClone();
        }

        var transformed = new JsonObject();
        foreach (var property in obj)
        {
            switch (property.Key)
            {
                case "instructions":
                case "input":
                case "previous_response_id":
                case "max_output_tokens":
                case "text":
                case "reasoning":
                case "background":
                case "conversation":
                case "include":
                case "max_tool_calls":
                case "prompt":
                case "store":
                case "truncation":
                    break;
                default:
                    transformed[property.Key] = property.Value?.DeepClone();
                    break;
            }
        }

        transformed["messages"] = BuildMessages(obj);

        if (obj["max_output_tokens"] is JsonNode maxOutputTokens)
        {
            transformed["max_completion_tokens"] = maxOutputTokens.DeepClone();
        }

        var reasoningEffort = NormalizeDeepSeekReasoningEffort(ExtractReasoningEffort(obj["reasoning"]));
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            transformed["reasoning_effort"] = reasoningEffort;
            transformed["thinking"] = new JsonObject { ["type"] = "enabled" };
        }

        if (obj["text"] is JsonObject text && text["format"] is JsonNode format)
        {
            transformed["response_format"] = format.DeepClone();
        }

        if (obj["stream"] is JsonValue streamValue &&
            streamValue.TryGetValue<bool>(out var stream) &&
            stream)
        {
            transformed["stream_options"] = MergeStreamOptions(obj["stream_options"]);
        }

        return transformed;
    }

    public void ApplyResponseHeaders(HttpResponseMessage upstreamResponse, UpstreamTarget target)
    {
    }

    private static JsonArray BuildMessages(JsonObject request)
    {
        var messages = new JsonArray();
        string? pendingReasoningContent = null;
        if (request["instructions"] is JsonNode instructions)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = ExtractText(instructions)
            });
        }

        if (request["input"] is JsonArray inputItems)
        {
            foreach (var item in inputItems)
            {
                if (item is JsonObject inputObject)
                {
                    var message = ConvertInputObject(inputObject, pendingReasoningContent);
                    pendingReasoningContent = null;
                    if (message is null)
                    {
                        pendingReasoningContent = MergeText(pendingReasoningContent, ExtractResponsesReasoningText(inputObject));
                        continue;
                    }

                    messages.Add(message);
                }
                else if (item is JsonValue inputValue && inputValue.TryGetValue<string>(out var text))
                {
                    messages.Add(CreateTextMessage("user", text));
                }
            }
        }
        else if (request["input"] is JsonValue inputValue && inputValue.TryGetValue<string>(out var text))
        {
            messages.Add(CreateTextMessage("user", text));
        }
        else if (request["messages"] is JsonArray existingMessages)
        {
            foreach (var message in existingMessages)
            {
                messages.Add(message?.DeepClone());
            }
        }

        return messages;
    }

    private static JsonObject? ConvertInputObject(JsonObject item, string? reasoningContent)
    {
        var type = item["type"]?.GetValue<string>();
        if (string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = item["call_id"]?.DeepClone(),
                ["content"] = ExtractText(item["output"])
            };
        }

        if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
        {
            var message = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = string.Empty,
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = item["call_id"]?.DeepClone(),
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = item["name"]?.DeepClone(),
                        ["arguments"] = item["arguments"]?.DeepClone() ?? "{}"
                    }
                })
            };
            if (!string.IsNullOrWhiteSpace(reasoningContent))
            {
                message["reasoning_content"] = reasoningContent;
            }

            return message;
        }

        var role = NormalizeRole(item["role"]?.GetValue<string>());
        var converted = CreateMessage(role, item["content"] ?? item.DeepClone());
        if (string.Equals(role, "assistant", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(reasoningContent))
        {
            converted["reasoning_content"] = reasoningContent;
        }

        return converted;
    }

    private static JsonObject CreateTextMessage(string role, string text) => new()
    {
        ["role"] = role,
        ["content"] = text
    };

    private static JsonObject CreateMessage(string role, JsonNode? content) => new()
    {
        ["role"] = role,
        ["content"] = ConvertContent(content)
    };

    private static JsonNode ConvertContent(JsonNode? content)
    {
        if (content is JsonArray array)
        {
            var parts = new JsonArray();
            foreach (var item in array)
            {
                if (item is not JsonObject obj)
                {
                    continue;
                }

                var type = obj["type"]?.GetValue<string>();
                if (string.Equals(type, "input_text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = obj["text"]?.DeepClone() ?? string.Empty
                    });
                }
                else if (string.Equals(type, "input_image", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = obj["image_url"]?.DeepClone() ?? obj["url"]?.DeepClone() ?? string.Empty
                        }
                    });
                }
            }

            return parts.Count == 0 ? ExtractText(content) : parts;
        }

        return ExtractText(content);
    }

    private static string ExtractText(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (node is JsonObject obj && obj["text"] is JsonValue textValue && textValue.TryGetValue<string>(out text))
        {
            return text;
        }

        if (node is JsonArray array)
        {
            return string.Join("\n", array
                .Select(ExtractText)
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        return node.ToJsonString();
    }

    private static string NormalizeRole(string? role) => role switch
    {
        "developer" => "system",
        "system" => "system",
        "assistant" => "assistant",
        "tool" => "tool",
        _ => "user"
    };

    private static string? ExtractReasoningEffort(JsonNode? reasoning)
    {
        return reasoning is JsonObject obj && obj["effort"] is JsonValue effort && effort.TryGetValue<string>(out var value)
            ? value
            : null;
    }

    private static string? ExtractResponsesReasoningText(JsonObject item)
    {
        return ExtractText(item["content"] ?? item["summary"] ?? item["text"]);
    }

    private static string? MergeText(string? existing, string? incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming))
        {
            return existing;
        }

        if (string.IsNullOrWhiteSpace(existing))
        {
            return incoming;
        }

        return existing + "\n" + incoming;
    }

    private static string? NormalizeDeepSeekReasoningEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        return effort.Trim().ToLowerInvariant() switch
        {
            "max" or "xhigh" => "max",
            "high" or "medium" or "low" => "high",
            _ => effort.Trim()
        };
    }

    private static JsonObject MergeStreamOptions(JsonNode? streamOptions)
    {
        var output = streamOptions is JsonObject obj
            ? obj.DeepClone().AsObject()
            : new JsonObject();
        output["include_usage"] = true;
        return output;
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
