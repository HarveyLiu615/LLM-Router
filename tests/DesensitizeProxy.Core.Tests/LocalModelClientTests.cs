using DesensitizeProxy.Core.Llm;
using DesensitizeProxy.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesensitizeProxy.Core.Tests;

public sealed class LocalModelClientTests
{
    [Fact]
    public async Task ExtractAsync_UsesOpenAiCompatibleChatCompletionsByDefault()
    {
        var handler = new CaptureHandler("{\"choices\":[{\"message\":{\"content\":\"[]\"}}]}");
        var client = CreateClient(handler, new PrivacyConfig());

        var result = await client.ExtractAsync("hello", CancellationToken.None);

        Assert.Equal("[]", result);
        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.RequestUri!.ToString());
        Assert.Contains("\"temperature\":0", handler.Body);
    }

    [Fact]
    public async Task ExtractAsync_UsesOllamaNativeApiChatWhenConfigured()
    {
        var config = new PrivacyConfig();
        config.LocalModel.Type = "ollama-native";
        var handler = new CaptureHandler("{\"message\":{\"content\":\"[]\"}}");
        var client = CreateClient(handler, config);

        var result = await client.ExtractAsync("hello", CancellationToken.None);

        Assert.Equal("[]", result);
        Assert.Equal("http://localhost:11434/api/chat", handler.RequestUri!.ToString());
        Assert.Contains("\"options\":{\"temperature\":0}", handler.Body);
    }

    private static LocalModelClient CreateClient(CaptureHandler handler, PrivacyConfig config)
    {
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        return new LocalModelClient(
            new HttpClient(handler),
            new PromptLoader(new TestHostEnvironment(), options),
            options,
            NullLogger<LocalModelClient>.Instance);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly string _response;

        public CaptureHandler(string response)
        {
            _response = response;
        }

        public Uri? RequestUri { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_response)
            };
        }
    }
}
