using DesensitizeProxy.Core.Abstractions;
using DesensitizeProxy.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DesensitizeProxy.Core.Llm;

public sealed class IChatClientPiiExtractionClient : ILocalPiiExtractionClient
{
    private readonly IChatClient _chatClient;
    private readonly PromptLoader _promptLoader;
    private readonly IOptionsMonitor<PrivacyConfig> _options;

    public IChatClientPiiExtractionClient(
        IChatClient chatClient,
        PromptLoader promptLoader,
        IOptionsMonitor<PrivacyConfig> options)
    {
        _chatClient = chatClient;
        _promptLoader = promptLoader;
        _options = options;
    }

    public async Task<string> ExtractAsync(string content, CancellationToken cancellationToken)
    {
        var config = _options.CurrentValue.LocalModel;
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, _promptLoader.Load()),
            new ChatMessage(ChatRole.User, content)
        };
        var response = await _chatClient.GetResponseAsync(messages, new ChatOptions
        {
            ModelId = config.Model,
            Temperature = 0,
            ResponseFormat = ChatResponseFormat.Json
        }, cancellationToken);

        return response.Text;
    }
}
