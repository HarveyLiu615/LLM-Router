using System.Text;
using System.Text.Json;
using DesensitizeProxy.AspNetCore.Middleware;
using DesensitizeProxy.AspNetCore.Yarp;
using DesensitizeProxy.Core.Abstractions;
using DesensitizeProxy.Core.Engine;
using DesensitizeProxy.Core.Models;
using DesensitizeProxy.Core.Redaction;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yarp.ReverseProxy.Forwarder;

namespace DesensitizeProxy.Core.Tests;

public sealed class HttpPipelineTests
{
    [Fact]
    public async Task Middleware_RedactsOpenAiRequestBeforeForwarding()
    {
        var config = TestConfig();
        config.LocalModel.Enabled = false;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var forwarder = new CapturingForwarder();
        var middleware = CreateMiddleware(options, redactor, forwarder);
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"gpt-test","messages":[{"role":"user","content":"电话13912345678 邮箱 a@example.com"}]}
        """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(forwarder.CapturedRequest);
        var body = await forwarder.CapturedRequest!.Content!.ReadAsStringAsync();
        Assert.Contains("[REDACTED:PHONE]", body);
        Assert.Contains("[REDACTED:EMAIL]", body);
        Assert.DoesNotContain("13912345678", body);
        Assert.Equal(new Uri("https://upstream.test/v1/chat/completions"), forwarder.CapturedRequest.RequestUri);
        Assert.True(forwarder.CapturedRequest.Headers.Authorization?.Scheme == "Bearer");
    }

    [Fact]
    public async Task Middleware_ForwardsClientModelWhenUsingDefaultUpstreamTarget()
    {
        var config = TestConfig();
        config.LocalModel.Enabled = false;
        config.Proxy.Targets = new Dictionary<string, UpstreamTarget>
        {
            ["default"] = new()
            {
                BaseUrl = "https://upstream.test/v1",
                ApiKey = "test-key",
                Provider = "openai"
            }
        };
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var forwarder = new CapturingForwarder();
        var middleware = CreateMiddleware(options, redactor, forwarder);
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"client-selected-model","messages":[{"role":"user","content":"hello"}]}
        """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.NotNull(forwarder.CapturedRequest);
        var body = await forwarder.CapturedRequest!.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("client-selected-model", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(new Uri("https://upstream.test/v1/chat/completions"), forwarder.CapturedRequest.RequestUri);
    }

    [Fact]
    public async Task Middleware_GeminiNativePathUsesClientModelWhenConfiguredTargetIsDefault()
    {
        var config = TestConfig();
        config.LocalModel.Enabled = false;
        config.Proxy.Targets = new Dictionary<string, UpstreamTarget>
        {
            ["default"] = new()
            {
                BaseUrl = "https://generativelanguage.googleapis.com",
                ApiKey = "test-key",
                Provider = "gemini"
            }
        };
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var forwarder = new CapturingForwarder();
        var middleware = CreateMiddleware(options, redactor, forwarder);
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"gemini-1.5-pro","messages":[{"role":"user","content":"hello"}]}
        """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.NotNull(forwarder.CapturedRequest);
        Assert.Equal(new Uri("https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-pro:generateContent"), forwarder.CapturedRequest!.RequestUri);
    }

    [Fact]
    public async Task Middleware_ForwardsModelsRequestWithoutJsonBodyParsing()
    {
        var config = TestConfig();
        config.LocalModel.Enabled = false;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var forwarder = new CapturingForwarder();
        var middleware = CreateMiddleware(options, redactor, forwarder);
        var context = CreateEmptyContext(HttpMethods.Get, "/v1/models");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(forwarder.CapturedRequest);
        Assert.Equal(HttpMethod.Get, forwarder.CapturedRequest!.Method);
        Assert.Equal(new Uri("https://upstream.test/v1/models"), forwarder.CapturedRequest.RequestUri);
        Assert.True(forwarder.CapturedRequest.Headers.Authorization?.Scheme == "Bearer");
    }

    [Fact]
    public async Task Middleware_LogsResponseBodyCanceledAsDebug()
    {
        var config = TestConfig();
        config.LocalModel.Enabled = false;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var logger = new CapturingLogger<DesensitizeProxyMiddleware>();
        var middleware = CreateMiddleware(
            options,
            redactor,
            new ErrorForwarder(ForwarderError.ResponseBodyCanceled),
            logger: logger);
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"gpt-test","messages":[{"role":"user","content":"hello"}]}
        """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Debug &&
            entry.Message.Contains("ResponseBodyCanceled", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Forwarder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Middleware_ForwardsGeminiModelListToGeminiTargetWithoutJsonBodyParsing()
    {
        var config = TestConfig();
        config.LocalModel.Enabled = false;
        config.Proxy.DefaultTarget = "openai";
        config.Proxy.Targets = new Dictionary<string, UpstreamTarget>
        {
            ["openai"] = new()
            {
                BaseUrl = "https://openai.test/v1",
                ApiKey = "openai-key",
                Provider = "openai"
            },
            ["gemini"] = new()
            {
                BaseUrl = "https://generativelanguage.googleapis.com",
                ApiKey = "gemini-key",
                Provider = "gemini"
            }
        };
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var forwarder = new CapturingForwarder();
        var middleware = CreateMiddleware(options, redactor, forwarder);
        var context = CreateEmptyContext(HttpMethods.Get, "/v1beta/models");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(forwarder.CapturedRequest);
        Assert.Equal(new Uri("https://generativelanguage.googleapis.com/v1beta/models"), forwarder.CapturedRequest!.RequestUri);
    }

    [Fact]
    public async Task Middleware_RedactsChunkedDeleteRequestWithoutContentLength()
    {
        var config = TestConfig();
        config.LocalModel.Enabled = false;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var forwarder = new CapturingForwarder();
        var middleware = CreateMiddleware(options, redactor, forwarder);
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"gpt-test","messages":[{"role":"user","content":"电话13912345678"}]}
        """);
        context.Request.Method = HttpMethods.Delete;
        context.Request.ContentLength = null;
        context.Request.Headers.TransferEncoding = "chunked";

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(forwarder.CapturedRequest);
        var body = await forwarder.CapturedRequest!.Content!.ReadAsStringAsync();
        Assert.Contains("[REDACTED:PHONE]", body);
        Assert.DoesNotContain("13912345678", body);
    }

    [Fact]
    public async Task Middleware_DoesNotWriteRedactionAuditLogWhenDisabled()
    {
        var config = TestConfig();
        config.LocalModel.Enabled = false;
        config.Observability.RedactionLoggingEnabled = false;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var auditLogger = new CapturingRedactionAuditLogger();
        var middleware = CreateMiddleware(options, redactor, new CapturingForwarder(), auditLogger: auditLogger);
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"gpt-test","messages":[{"role":"user","content":"电话13912345678 邮箱 a@example.com"}]}
        """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Empty(auditLogger.Entries);
    }

    [Fact]
    public async Task Middleware_WritesRedactionAuditLogWithoutOriginalValuesWhenEnabled()
    {
        var config = TestConfig();
        config.LocalModel.Enabled = false;
        config.Observability.RedactionLoggingEnabled = true;
        config.Observability.RedactionLogIncludeValues = false;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var auditLogger = new CapturingRedactionAuditLogger();
        var middleware = CreateMiddleware(options, redactor, new CapturingForwarder(), auditLogger: auditLogger);
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"gpt-test","messages":[{"role":"user","content":"电话13912345678 邮箱 a@example.com"}]}
        """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Contains(auditLogger.Entries, entry => entry.Label == "PHONE");
        Assert.Contains(auditLogger.Entries, entry => entry.Label == "EMAIL");
        Assert.All(auditLogger.Entries, entry => Assert.Null(entry.OriginalValue));
    }

    [Fact]
    public async Task Middleware_WritesRegexOriginalValuesOnlyWhenExplicitlyEnabled()
    {
        var config = TestConfig();
        config.LocalModel.Enabled = false;
        config.Observability.RedactionLoggingEnabled = true;
        config.Observability.RedactionLogIncludeValues = true;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var auditLogger = new CapturingRedactionAuditLogger();
        var middleware = CreateMiddleware(options, redactor, new CapturingForwarder(), auditLogger: auditLogger);
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"gpt-test","messages":[{"role":"user","content":"电话13912345678 邮箱 a@example.com"}]}
        """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Contains(auditLogger.Entries, entry => entry is { Label: "PHONE", OriginalValue: "13912345678", RedactedValue: "[REDACTED:PHONE]" });
        Assert.Contains(auditLogger.Entries, entry => entry is { Label: "EMAIL", OriginalValue: "a@example.com", RedactedValue: "[REDACTED:EMAIL]" });
    }

    [Fact]
    public async Task Middleware_WritesLlmOriginalValuesOnlyWhenExplicitlyEnabled()
    {
        var config = TestConfig();
        config.LocalModel.Enabled = true;
        config.Observability.RedactionLoggingEnabled = true;
        config.Observability.RedactionLogIncludeValues = true;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var auditLogger = new CapturingRedactionAuditLogger();
        var middleware = CreateMiddleware(
            options,
            redactor,
            new CapturingForwarder(),
            new FixedLlmDesensitizer(new DesensitizeResult(
                "客户[REDACTED:NAME]住在北京",
                WasModelUsed: true,
                DesensitizeStatus.Success,
                null,
                [new RedactionHit("llm", "NAME", "张三", "[REDACTED:NAME]", Count: 1)])),
            auditLogger: auditLogger);
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"gpt-test","messages":[{"role":"user","content":"客户张三住在北京，身份证号是 430102199001011234"}]}
        """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Contains(auditLogger.Entries, entry => entry is { OriginalValue: "张三", RedactedValue: "[REDACTED:NAME]" });
    }

    [Fact]
    public async Task Middleware_Returns413WhenBodyExceedsLimit()
    {
        var config = TestConfig();
        config.MaxBodySizeBytes = 10;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var middleware = CreateMiddleware(options, redactor, new CapturingForwarder());
        var context = CreateJsonContext("/v1/chat/completions", "{\"model\":\"gpt-test\",\"messages\":[]}");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_Returns413WhenStreamingBodyExceedsLimitWithoutContentLength()
    {
        var config = TestConfig();
        config.MaxBodySizeBytes = 10;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var middleware = CreateMiddleware(options, redactor, new CapturingForwarder());
        var context = CreateJsonContext("/v1/chat/completions", "{\"model\":\"gpt-test\",\"messages\":[]}");
        context.Request.ContentLength = null;

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_StrictModeBlocksWhenLocalModelDisabledAndTriggerHit()
    {
        var config = TestConfig();
        config.StrictMode = true;
        config.LocalModel.Enabled = false;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var middleware = CreateMiddleware(options, redactor, new CapturingForwarder());
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"gpt-test","messages":[{"role":"user","content":"我的身份证号是 430102199001011234"}]}
        """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_StrictModeBlocksHintWhenLocalModelDisabled()
    {
        var config = TestConfig();
        config.StrictMode = true;
        config.LocalModel.Enabled = false;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var middleware = CreateMiddleware(options, redactor, new CapturingForwarder());
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"gpt-test","messages":[{"role":"user","content":"帮我解释一下 password reset 流程"}]}
        """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_StrictModeBlocksWhenLlmReturnsParseFailure()
    {
        var config = TestConfig();
        config.StrictMode = true;
        config.LocalModel.Enabled = true;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var middleware = CreateMiddleware(
            options,
            redactor,
            new CapturingForwarder(),
            new FixedLlmDesensitizer(new DesensitizeResult("original", true, DesensitizeStatus.ParseFailure, "invalid json array")));
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"gpt-test","messages":[{"role":"user","content":"我的身份证号是 430102199001011234"}]}
        """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_StrictModeReturns413WhenTriggeredTextPartIsTooLongForLlm()
    {
        var config = TestConfig();
        config.StrictMode = true;
        config.MaxTextPartLengthForLlm = 10;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var middleware = CreateMiddleware(options, redactor, new CapturingForwarder());
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"gpt-test","messages":[{"role":"user","content":"我的身份证号是 430102199001011234"}]}
        """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_StrictModeBlocksSystemMessageWhenSystemLlmIsEnabledAndLocalModelDisabled()
    {
        var config = TestConfig();
        config.StrictMode = true;
        config.LocalModel.Enabled = false;
        config.SystemMessageRedaction.RuleEngine = true;
        config.SystemMessageRedaction.Llm = true;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var middleware = CreateMiddleware(options, redactor, new CapturingForwarder());
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"gpt-test","messages":[{"role":"system","content":"用户住址是北京市朝阳区xx路xx号"}]}
        """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_PreservesStreamFlagAndAcceptHeaderForSseForwarding()
    {
        var config = TestConfig();
        config.LocalModel.Enabled = false;
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var forwarder = new CapturingForwarder();
        var middleware = CreateMiddleware(options, redactor, forwarder);
        var context = CreateJsonContext("/v1/chat/completions", """
        {"model":"gpt-test","stream":true,"messages":[{"role":"user","content":"hello"}]}
        """);
        context.Request.Headers.Accept = "text/event-stream";

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.NotNull(forwarder.CapturedRequest);
        var body = await forwarder.CapturedRequest!.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Contains(forwarder.CapturedRequest.Headers.Accept, h => h.MediaType == "text/event-stream");
    }

    private static DesensitizeProxyMiddleware CreateMiddleware(
        TestOptionsMonitor<PrivacyConfig> options,
        IPiiRedactor redactor,
        IHttpForwarder forwarder,
        ILlmDesensitizer? llmDesensitizer = null,
        IRedactionAuditLogger? auditLogger = null,
        ILogger<DesensitizeProxyMiddleware>? logger = null)
    {
        return new DesensitizeProxyMiddleware(
            new RuleEngine(redactor, options),
            llmDesensitizer ?? new NoopLlmDesensitizer(),
            redactor,
            new SchemaCleaner(),
            forwarder,
            new HttpMessageInvoker(new SocketsHttpHandler()),
            options,
            logger ?? NullLogger<DesensitizeProxyMiddleware>.Instance,
            new DesensitizeMetrics(),
            new UpstreamResolver(),
            new ProviderTransformerRegistry(),
            auditLogger ?? new CapturingRedactionAuditLogger());
    }

    private static DefaultHttpContext CreateJsonContext(string path, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DefaultHttpContext CreateEmptyContext(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static PrivacyConfig TestConfig() => new()
    {
        MaxBodySizeBytes = PrivacyConfig.DefaultMaxBodySizeBytes,
        Proxy = new ProxyConfig
        {
            Targets = new Dictionary<string, UpstreamTarget>
            {
                ["gpt-test"] = new()
                {
                    BaseUrl = "https://upstream.test/v1",
                    ApiKey = "test-key",
                    Provider = "openai"
                }
            }
        }
    };

    private sealed class NoopLlmDesensitizer : ILlmDesensitizer
    {
        public Task<DesensitizeResult> DesensitizeAsync(string content, DetectionResult detection, CancellationToken cancellationToken) =>
            Task.FromResult(new DesensitizeResult(content, false, DesensitizeStatus.ModelFailure, "noop"));

        public Task<IReadOnlyList<DesensitizeResult>> DesensitizeBatchAsync(
            IReadOnlyList<(string Content, DetectionResult Detection)> items,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DesensitizeResult>>(items
                .Select(item => new DesensitizeResult(item.Content, false, DesensitizeStatus.ModelFailure, "noop"))
                .ToList());
    }

    private sealed class FixedLlmDesensitizer : ILlmDesensitizer
    {
        private readonly DesensitizeResult _result;

        public FixedLlmDesensitizer(DesensitizeResult result)
        {
            _result = result;
        }

        public Task<DesensitizeResult> DesensitizeAsync(string content, DetectionResult detection, CancellationToken cancellationToken) =>
            Task.FromResult(_result);

        public Task<IReadOnlyList<DesensitizeResult>> DesensitizeBatchAsync(
            IReadOnlyList<(string Content, DetectionResult Detection)> items,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DesensitizeResult>>(items.Select(_ => _result).ToList());
    }

    private sealed class CapturingForwarder : IHttpForwarder
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }

        public async ValueTask<ForwarderError> SendAsync(
            HttpContext context,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer)
        {
            var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), destinationPrefix);
            await transformer.TransformRequestAsync(context, request, destinationPrefix, context.RequestAborted);
            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
            }

            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            request.Content = new StringContent(body, Encoding.UTF8, context.Request.ContentType ?? "application/json");
            CapturedRequest = request;
            context.Response.StatusCode = StatusCodes.Status200OK;
            return ForwarderError.None;
        }

        public ValueTask<ForwarderError> SendAsync(
            HttpContext context,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer,
            CancellationToken cancellationToken)
        {
            return SendAsync(context, destinationPrefix, httpClient, requestConfig, transformer);
        }
    }

    private sealed class ErrorForwarder : IHttpForwarder
    {
        private readonly ForwarderError _error;

        public ErrorForwarder(ForwarderError error)
        {
            _error = error;
        }

        public ValueTask<ForwarderError> SendAsync(
            HttpContext context,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer)
        {
            context.Features.Set<IForwarderErrorFeature>(new TestForwarderErrorFeature(_error, new TaskCanceledException("canceled")));
            return ValueTask.FromResult(_error);
        }

        public ValueTask<ForwarderError> SendAsync(
            HttpContext context,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer,
            CancellationToken cancellationToken)
        {
            return SendAsync(context, destinationPrefix, httpClient, requestConfig, transformer);
        }
    }

    private sealed class TestForwarderErrorFeature : IForwarderErrorFeature
    {
        public TestForwarderErrorFeature(ForwarderError error, Exception exception)
        {
            Error = error;
            Exception = exception;
        }

        public ForwarderError Error { get; }

        public Exception Exception { get; }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingRedactionAuditLogger : IRedactionAuditLogger
    {
        public List<RedactionAuditEntry> Entries { get; } = [];

        public void Log(RedactionAuditEntry entry) => Entries.Add(entry);
    }
}
