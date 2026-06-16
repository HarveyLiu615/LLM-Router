using System.Text.Json.Nodes;
using DesensitizeProxy.AspNetCore.Yarp;
using DesensitizeProxy.Core.Models;
using Microsoft.AspNetCore.Http;

namespace DesensitizeProxy.Core.Tests;

public sealed class ProviderTransformerTests
{
    [Fact]
    public void AnthropicTransformer_MovesSystemMessagesToSystemField()
    {
        var transformer = new AnthropicTransformer();
        var request = JsonNode.Parse("""
        {"model":"claude","messages":[{"role":"system","content":"sys"},{"role":"user","content":"hello"}],"stream":true,
         "tools":[{"type":"function","function":{"name":"lookup","description":"d","parameters":{"type":"object"}}}]}
        """)!;

        var result = transformer.TransformRequest(request, Target()).AsObject();

        Assert.Equal("sys", result["system"]!.GetValue<string>());
        Assert.Single(result["messages"]!.AsArray());
        Assert.True(result["stream"]!.GetValue<bool>());
        Assert.Equal("lookup", result["tools"]![0]!["name"]!.GetValue<string>());
        Assert.NotNull(result["tools"]![0]!["input_schema"]);
    }

    [Fact]
    public void GeminiTransformer_ConvertsOpenAiMessagesToContents()
    {
        var transformer = new GeminiTransformer();
        var request = JsonNode.Parse("""
        {"model":"gemini-1.5-pro","messages":[{"role":"system","content":"sys"},{"role":"user","content":"hello"},{"role":"assistant","content":"hi"}],
         "tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object"}}}]}
        """)!;

        var result = transformer.TransformRequest(request, Target()).AsObject();

        Assert.Equal(2, result["contents"]!.AsArray().Count);
        Assert.NotNull(result["systemInstruction"]);
        Assert.Equal("lookup", result["tools"]![0]!["functionDeclarations"]![0]!["name"]!.GetValue<string>());
        Assert.Contains("generateContent", transformer.BuildPath(new PathString("/v1/chat/completions"), QueryString.Empty, request, Target()));
    }

    [Fact]
    public void GeminiTransformer_UsesClientModelFieldWhenPathDoesNotContainModel()
    {
        var transformer = new GeminiTransformer();
        var request = JsonNode.Parse("""
        {"model":"gemini-1.5-pro","messages":[{"role":"user","content":"hello"}]}
        """)!;

        var path = transformer.BuildPath(new PathString("/v1/chat/completions"), QueryString.Empty, request, Target());

        Assert.Equal("/v1beta/models/gemini-1.5-pro:generateContent", path);
    }

    [Fact]
    public void GeminiTransformer_ThrowsWhenNativePathAndRequestDoNotProvideModel()
    {
        var transformer = new GeminiTransformer();
        var request = JsonNode.Parse("""{"messages":[{"role":"user","content":"hello"}]}""")!;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            transformer.BuildPath(new PathString("/v1/chat/completions"), QueryString.Empty, request, Target()));

        Assert.Contains("model", ex.Message);
    }

    [Fact]
    public void GeminiTransformer_UsesStreamGenerateContentForStreamPath()
    {
        var transformer = new GeminiTransformer();

        var path = transformer.BuildPath(new PathString("/v1/models/gemini-pro:streamGenerateContent"), QueryString.Empty, JsonNode.Parse("{}")!, Target());

        Assert.Contains(":streamGenerateContent", path);
    }

    [Fact]
    public void GeminiTransformer_PreservesModelListPathWithoutRequestModel()
    {
        var transformer = new GeminiTransformer();

        var path = transformer.BuildPath(new PathString("/v1beta/models"), new QueryString("?pageSize=10"), request: null, Target());

        Assert.Equal("/v1beta/models?pageSize=10", path);
    }

    [Fact]
    public void AnthropicTransformer_PreservesQueryWhenConvertingChatCompletionsPath()
    {
        var transformer = new AnthropicTransformer();

        var path = transformer.BuildPath(new PathString("/v1/chat/completions"), new QueryString("?beta=true"), JsonNode.Parse("{}")!, Target());

        Assert.Equal("/v1/messages?beta=true", path);
    }

    [Fact]
    public void GeminiTransformer_MarksResponseAsProviderNative()
    {
        var transformer = new GeminiTransformer();
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);

        transformer.ApplyResponseHeaders(response, new UpstreamTarget
        {
            BaseUrl = "https://generativelanguage.googleapis.com",
            ApiKey = "test",
            Provider = "gemini"
        });

        Assert.True(response.Headers.TryGetValues("x-desensitize-response-mode", out var values));
        Assert.Contains("provider-native", values);
    }

    [Fact]
    public void OpenAiTransformer_RemovesBasePathWhenBaseUrlAlreadyContainsVersion()
    {
        var transformer = new OpenAiCompatibleTransformer();
        var target = new UpstreamTarget
        {
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "test",
            Provider = "openai"
        };

        var path = transformer.BuildPath(new PathString("/v1/chat/completions"), QueryString.Empty, JsonNode.Parse("{}")!, target);

        Assert.Equal("/chat/completions", path);
    }

    [Fact]
    public void DeepSeekTransformer_ConvertsResponsesRequestToChatCompletions()
    {
        var transformer = new DeepSeekTransformer();
        var target = new UpstreamTarget
        {
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKey = "test",
            Provider = "deepseek"
        };
        var request = JsonNode.Parse("""
        {
          "model":"deepseek-reasoner",
          "instructions":"sys",
          "input":[{"role":"user","content":[{"type":"input_text","text":"hello"}]}],
          "max_output_tokens":512,
          "reasoning":{"effort":"xhigh"},
          "text":{"format":{"type":"json_object"}},
          "stream":true,
          "stream_options":{"include_usage":false},
          "store":true,
          "truncation":"auto"
        }
        """)!;

        var path = transformer.BuildPath(new PathString("/v1/responses"), QueryString.Empty, request, target);
        var result = transformer.TransformRequest(request, target).AsObject();

        Assert.Equal("/chat/completions", path);
        Assert.Equal("deepseek-reasoner", result["model"]!.GetValue<string>());
        Assert.Null(result["input"]);
        Assert.Null(result["instructions"]);
        Assert.Null(result["store"]);
        Assert.Null(result["truncation"]);
        Assert.Equal(512, result["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal("max", result["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("enabled", result["thinking"]!["type"]!.GetValue<string>());
        Assert.True(result["stream_options"]!["include_usage"]!.GetValue<bool>());
        Assert.Equal("sys", result["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("user", result["messages"]![1]!["role"]!.GetValue<string>());
        Assert.Equal("hello", result["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("json_object", result["response_format"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void DeepSeekTransformer_CarriesReasoningContentForToolCalls()
    {
        var transformer = new DeepSeekTransformer();
        var request = JsonNode.Parse("""
        {
          "model":"deepseek-v4-pro",
          "input":[
            {"type":"reasoning","content":[{"type":"reasoning_text","text":"need a tool"}]},
            {"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{\"q\":\"x\"}"},
            {"type":"function_call_output","call_id":"call_1","output":"ok"}
          ],
          "reasoning":{"effort":"low"}
        }
        """)!;

        var result = transformer.TransformRequest(request, Target()).AsObject();

        Assert.Equal("high", result["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("enabled", result["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("assistant", result["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("need a tool", result["messages"]![0]!["reasoning_content"]!.GetValue<string>());
        Assert.Equal("lookup", result["messages"]![0]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("tool", result["messages"]![1]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", result["messages"]![1]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public void Registry_ResolvesDeepSeekTransformer()
    {
        var registry = new ProviderTransformerRegistry();

        var transformer = registry.Resolve(new UpstreamTarget
        {
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKey = "test",
            Provider = "deepseek"
        });

        Assert.IsType<DeepSeekTransformer>(transformer);
    }

    private static UpstreamTarget Target() => new()
    {
        BaseUrl = "https://example.com",
        ApiKey = "test",
        Provider = "openai-compatible"
    };
}
