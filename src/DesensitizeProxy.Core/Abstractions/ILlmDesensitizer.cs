using DesensitizeProxy.Core.Models;

namespace DesensitizeProxy.Core.Abstractions;

public interface ILlmDesensitizer
{
    Task<DesensitizeResult> DesensitizeAsync(
        string content,
        DetectionResult detection,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DesensitizeResult>> DesensitizeBatchAsync(
        IReadOnlyList<(string Content, DetectionResult Detection)> items,
        CancellationToken cancellationToken);
}
