using DesensitizeProxy.Core.Models;

namespace DesensitizeProxy.Core.Abstractions;

public interface IRuleEngine
{
    DetectionResult Check(string content);
}
