using DesensitizeProxy.Core.Models;

namespace DesensitizeProxy.Core.Abstractions;

public interface IPiiRedactor
{
    string Redact(string content);
    RedactionResult RedactWithHits(string content);
    string RedactPhase1Only(string content);
    string RedactSystem(string content, SystemMessageRedactionConfig config);
    RedactionResult RedactSystemWithHits(string content, SystemMessageRedactionConfig config);
    bool HasAnyHit(string content);
}
