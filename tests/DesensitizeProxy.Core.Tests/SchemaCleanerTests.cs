using System.Text.Json.Nodes;
using DesensitizeProxy.Core.Redaction;

namespace DesensitizeProxy.Core.Tests;

public sealed class SchemaCleanerTests
{
    [Fact]
    public void Clean_RemovesUnsupportedKeywordsButKeepsAdditionalPropertiesForOpenAi()
    {
        var cleaner = new SchemaCleaner();
        var tools = JsonNode.Parse("""
        [{"function":{"parameters":{"type":"object","patternProperties":{},"additionalProperties":{"type":"string"},"properties":{"name":{"type":"string","minLength":1}}}}}]
        """)!;

        var cleaned = cleaner.Clean(tools, "openai").ToJsonString();

        Assert.DoesNotContain("patternProperties", cleaned);
        Assert.DoesNotContain("minLength", cleaned);
        Assert.Contains("additionalProperties", cleaned);
    }

    [Fact]
    public void Clean_RemovesAdditionalPropertiesForGemini()
    {
        var cleaner = new SchemaCleaner();
        var tools = JsonNode.Parse("""
        [{"functionDeclarations":[{"parameters":{"type":"object","additionalProperties":{"type":"string"}}}]}]
        """)!;

        var cleaned = cleaner.Clean(tools, "gemini").ToJsonString();

        Assert.DoesNotContain("additionalProperties", cleaned);
    }
}
