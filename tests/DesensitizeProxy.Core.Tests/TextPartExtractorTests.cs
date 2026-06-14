using System.Text.Json.Nodes;
using DesensitizeProxy.AspNetCore.Middleware;

namespace DesensitizeProxy.Core.Tests;

public sealed class TextPartExtractorTests
{
    [Fact]
    public void Extract_GetsOpenAiStringAndMultimodalTextParts()
    {
        var json = JsonNode.Parse("""
        {"messages":[
          {"role":"system","content":"sys"},
          {"role":"user","content":[{"type":"text","text":"hello"},{"type":"image_url","image_url":{"url":"x"}}]}
        ]}
        """)!;

        var parts = TextPartExtractor.Extract(json);

        Assert.Equal(2, parts.Count);
        Assert.True(parts[0].IsSystem);
        Assert.Equal("hello", parts[1].Text);
    }

    [Fact]
    public void Extract_GetsGeminiTextAndSystemInstruction()
    {
        var json = JsonNode.Parse("""
        {"systemInstruction":{"parts":[{"text":"sys"}]},"contents":[{"parts":[{"text":"hello"},{"inlineData":{}}]}]}
        """)!;

        var parts = TextPartExtractor.Extract(json);

        Assert.Equal(2, parts.Count);
        Assert.Contains(parts, part => part.IsSystem && part.Text == "sys");
        Assert.Contains(parts, part => !part.IsSystem && part.Text == "hello");
    }

    [Fact]
    public void Extract_GetsOpenAiResponsesInputString()
    {
        var json = JsonNode.Parse("""{"input":"hello 13912345678"}""")!;

        var part = Assert.Single(TextPartExtractor.Extract(json));
        part.SetText("redacted");

        Assert.Equal("hello 13912345678", part.Text);
        Assert.Equal("redacted", json["input"]!.GetValue<string>());
    }

    [Fact]
    public void Extract_GetsOpenAiResponsesContentArrayText()
    {
        var json = JsonNode.Parse("""
        {"input":[{"role":"developer","content":[{"type":"input_text","text":"sys"}]},{"role":"user","content":[{"type":"input_text","text":"hello"},{"type":"input_image","image_url":"x"}]}]}
        """)!;

        var parts = TextPartExtractor.Extract(json);

        Assert.Equal(2, parts.Count);
        Assert.True(parts[0].IsSystem);
        Assert.Equal("hello", parts[1].Text);
    }
}
