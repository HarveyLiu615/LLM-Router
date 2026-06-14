using DesensitizeProxy.Core.Llm;
using DesensitizeProxy.Core.Models;
using Microsoft.Extensions.AI;

namespace DesensitizeProxy.Core.Tests;

public sealed class IChatClientPiiExtractionClientTests
{
    [Fact]
    public async Task ExtractAsync_UsesIChatClientAndReturnsResponseText()
    {
        var options = new TestOptionsMonitor<PrivacyConfig>(new PrivacyConfig());
        var chatClient = new StubChatClient("[{\"type\":\"NAME\",\"value\":\"张三\"}]");
        var client = new IChatClientPiiExtractionClient(
            chatClient,
            new PromptLoader(new TestHostEnvironment(), options),
            options);

        var result = await client.ExtractAsync("张三", CancellationToken.None);

        Assert.Contains("张三", result);
        Assert.Equal("openbmb/minicpm4.1", chatClient.LastOptions?.ModelId);
    }

    private sealed class StubChatClient : IChatClient
    {
        private readonly string _response;

        public StubChatClient(string response)
        {
            _response = response;
        }

        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
