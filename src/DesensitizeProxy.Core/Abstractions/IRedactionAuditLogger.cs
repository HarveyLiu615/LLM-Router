using DesensitizeProxy.Core.Models;

namespace DesensitizeProxy.Core.Abstractions;

public interface IRedactionAuditLogger
{
    void Log(RedactionAuditEntry entry);
}
