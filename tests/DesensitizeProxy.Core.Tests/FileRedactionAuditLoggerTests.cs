using System.Text.Json;
using DesensitizeProxy.Core.Models;
using DesensitizeProxy.Core.Observability;
using DesensitizeProxy.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesensitizeProxy.Core.Tests;

public sealed class FileRedactionAuditLoggerTests
{
    [Fact]
    public void Log_WritesJsonLineToDatedFileUnderLogDirectory()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "locrouter-tests", Guid.NewGuid().ToString("N"));
        var config = new PrivacyConfig
        {
            Runtime = new RuntimeConfig
            {
                LogDirectory = logDirectory
            }
        };
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var directories = new RuntimeDirectoryResolver(new TestHostEnvironment(), options);
        var logger = new FileRedactionAuditLogger(directories, NullLogger<FileRedactionAuditLogger>.Instance);

        logger.Log(new RedactionAuditEntry(
            new DateTimeOffset(2026, 6, 13, 8, 0, 0, TimeSpan.FromHours(8)),
            "regex",
            "messages[0].content",
            IsSystem: false,
            "phase2",
            "PHONE",
            Count: 1,
            "13912345678",
            "[REDACTED:PHONE]"));

        var logPath = Path.Combine(logDirectory, "log", "redactions-2026-06-13.log");
        var line = File.ReadLines(logPath).Single();
        using var doc = JsonDocument.Parse(line);

        Assert.Equal("regex", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal("2026-06-13T08:00:00+08:00", doc.RootElement.GetProperty("timestamp").GetString());
        Assert.Equal("messages[0].content", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal("phase2", doc.RootElement.GetProperty("phase").GetString());
        Assert.Equal("PHONE", doc.RootElement.GetProperty("label").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("13912345678", doc.RootElement.GetProperty("originalValue").GetString());
        Assert.Equal("[REDACTED:PHONE]", doc.RootElement.GetProperty("redactedValue").GetString());
    }

    [Fact]
    public void Log_WritesOriginalValueWithReadableUnicodeCharacters()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "locrouter-tests", Guid.NewGuid().ToString("N"));
        var config = new PrivacyConfig
        {
            Runtime = new RuntimeConfig
            {
                LogDirectory = logDirectory
            }
        };
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var directories = new RuntimeDirectoryResolver(new TestHostEnvironment(), options);
        var logger = new FileRedactionAuditLogger(directories, NullLogger<FileRedactionAuditLogger>.Instance);

        logger.Log(new RedactionAuditEntry(
            new DateTimeOffset(2026, 6, 14, 8, 0, 0, TimeSpan.FromHours(8)),
            "regex",
            "input[0].content[0].text",
            IsSystem: false,
            "phase3",
            "API_KEY",
            Count: 1,
            "...`、`api_key:",
            "[REDACTED:API_KEY]"));

        var logPath = Path.Combine(logDirectory, "log", "redactions-2026-06-14.log");
        var line = File.ReadLines(logPath).Single();
        using var doc = JsonDocument.Parse(line);

        Assert.Contains("...`、`api_key:", line);
        Assert.DoesNotContain("\\u0060", line);
        Assert.DoesNotContain("\\u3001", line);
        Assert.Equal("...`、`api_key:", doc.RootElement.GetProperty("originalValue").GetString());
    }
}
