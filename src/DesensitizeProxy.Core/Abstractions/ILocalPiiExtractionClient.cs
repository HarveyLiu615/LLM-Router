namespace DesensitizeProxy.Core.Abstractions;

public interface ILocalPiiExtractionClient
{
    Task<string> ExtractAsync(string content, CancellationToken cancellationToken);
}
