using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DesensitizeProxy.Core.Abstractions;
using DesensitizeProxy.Core.Models;
using DesensitizeProxy.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace DesensitizeProxy.Core.Observability;

public sealed class FileRedactionAuditLogger : IRedactionAuditLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly RuntimeDirectoryResolver _directories;
    private readonly ILogger<FileRedactionAuditLogger> _logger;
    private readonly object _gate = new();

    public FileRedactionAuditLogger(
        RuntimeDirectoryResolver directories,
        ILogger<FileRedactionAuditLogger> logger)
    {
        _directories = directories;
        _logger = logger;
    }

    public void Log(RedactionAuditEntry entry)
    {
        try
        {
            var logDirectory = Path.Combine(_directories.LogDirectory, "log");
            Directory.CreateDirectory(logDirectory);
            var path = Path.Combine(logDirectory, $"redactions-{entry.Timestamp:yyyy-MM-dd}.log");
            var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
            lock (_gate)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to write redaction audit log");
        }
    }
}
