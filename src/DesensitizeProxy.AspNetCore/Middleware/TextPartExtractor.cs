using System.Text.Json.Nodes;

namespace DesensitizeProxy.AspNetCore.Middleware;

public static class TextPartExtractor
{
    public static IReadOnlyList<TextPart> Extract(JsonNode root)
    {
        var parts = new List<TextPart>();
        ExtractOpenAiMessages(root, parts);
        ExtractOpenAiResponses(root, parts);
        ExtractGeminiContents(root, parts);
        return parts;
    }

    private static void ExtractOpenAiMessages(JsonNode root, List<TextPart> parts)
    {
        if (root["messages"] is not JsonArray messages)
        {
            return;
        }

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject message)
            {
                continue;
            }

            var role = message["role"]?.GetValue<string>();
            var isSystem = string.Equals(role, "system", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase);

            if (message["content"] is JsonValue value && value.TryGetValue<string>(out var content))
            {
                var path = $"messages[{i}].content";
                parts.Add(new TextPart
                {
                    Ref = new TextPartRef(path),
                    Text = content,
                    IsSystem = isSystem,
                    SetText = text => message["content"] = text
                });
            }
            else if (message["content"] is JsonArray contentParts)
            {
                for (var j = 0; j < contentParts.Count; j++)
                {
                    if (contentParts[j] is not JsonObject part ||
                        !string.Equals(part["type"]?.GetValue<string>(), "text", StringComparison.OrdinalIgnoreCase) ||
                        part["text"] is not JsonValue textValue ||
                        !textValue.TryGetValue<string>(out var text))
                    {
                        continue;
                    }

                    var path = $"messages[{i}].content[{j}].text";
                    parts.Add(new TextPart
                    {
                        Ref = new TextPartRef(path),
                        Text = text,
                        IsSystem = isSystem,
                        SetText = replacement => part["text"] = replacement
                    });
                }
            }
        }
    }

    private static void ExtractOpenAiResponses(JsonNode root, List<TextPart> parts)
    {
        if (root["input"] is JsonValue inputValue && inputValue.TryGetValue<string>(out var inputText))
        {
            parts.Add(new TextPart
            {
                Ref = new TextPartRef("input"),
                Text = inputText,
                IsSystem = false,
                SetText = replacement => root["input"] = replacement
            });
            return;
        }

        if (root["input"] is not JsonArray input)
        {
            return;
        }

        for (var i = 0; i < input.Count; i++)
        {
            if (input[i] is not JsonObject item)
            {
                continue;
            }

            var role = item["role"]?.GetValue<string>();
            var isSystem = string.Equals(role, "system", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase);
            if (item["content"] is JsonValue value && value.TryGetValue<string>(out var contentText))
            {
                var path = $"input[{i}].content";
                parts.Add(new TextPart
                {
                    Ref = new TextPartRef(path),
                    Text = contentText,
                    IsSystem = isSystem,
                    SetText = replacement => item["content"] = replacement
                });
                continue;
            }

            if (item["content"] is not JsonArray content)
            {
                continue;
            }

            for (var j = 0; j < content.Count; j++)
            {
                if (content[j] is not JsonObject part ||
                    !IsResponsesTextType(part["type"]?.GetValue<string>()) ||
                    part["text"] is not JsonValue textValue ||
                    !textValue.TryGetValue<string>(out var text))
                {
                    continue;
                }

                var path = $"input[{i}].content[{j}].text";
                parts.Add(new TextPart
                {
                    Ref = new TextPartRef(path),
                    Text = text,
                    IsSystem = isSystem,
                    SetText = replacement => part["text"] = replacement
                });
            }
        }
    }

    private static bool IsResponsesTextType(string? type) =>
        string.Equals(type, "input_text", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "text", StringComparison.OrdinalIgnoreCase);

    private static void ExtractGeminiContents(JsonNode root, List<TextPart> parts)
    {
        if (root["contents"] is not JsonArray contents)
        {
            return;
        }

        for (var i = 0; i < contents.Count; i++)
        {
            if (contents[i] is not JsonObject content || content["parts"] is not JsonArray geminiParts)
            {
                continue;
            }

            for (var j = 0; j < geminiParts.Count; j++)
            {
                if (geminiParts[j] is not JsonObject part ||
                    part["text"] is not JsonValue textValue ||
                    !textValue.TryGetValue<string>(out var text))
                {
                    continue;
                }

                var path = $"contents[{i}].parts[{j}].text";
                parts.Add(new TextPart
                {
                    Ref = new TextPartRef(path),
                    Text = text,
                    IsSystem = false,
                    SetText = replacement => part["text"] = replacement
                });
            }
        }

        if (root["systemInstruction"] is JsonObject systemInstruction &&
            systemInstruction["parts"] is JsonArray systemParts)
        {
            for (var i = 0; i < systemParts.Count; i++)
            {
                if (systemParts[i] is not JsonObject part ||
                    part["text"] is not JsonValue textValue ||
                    !textValue.TryGetValue<string>(out var text))
                {
                    continue;
                }

                var path = $"systemInstruction.parts[{i}].text";
                parts.Add(new TextPart
                {
                    Ref = new TextPartRef(path),
                    Text = text,
                    IsSystem = true,
                    SetText = replacement => part["text"] = replacement
                });
            }
        }
    }
}
