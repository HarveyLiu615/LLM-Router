using DesensitizeProxy.Core.Llm;

namespace DesensitizeProxy.Core.Tests;

public sealed class PiiJsonParserTests
{
    [Fact]
    public void Parse_AcceptsMarkdownFenceSingleQuotesAndTrailingComma()
    {
        var parser = new PiiJsonParser();

        var result = parser.Parse("```json\n[{'type':'NAME','value':'张三',},]\n```");

        Assert.NotNull(result);
        var item = Assert.Single(result!);
        Assert.Equal("NAME", item.Type);
        Assert.Equal("张三", item.Value);
    }

    [Fact]
    public void Parse_ReturnsNullForNonArrayJson()
    {
        var parser = new PiiJsonParser();

        var result = parser.Parse("{\"type\":\"NAME\"}");

        Assert.Null(result);
    }
}
