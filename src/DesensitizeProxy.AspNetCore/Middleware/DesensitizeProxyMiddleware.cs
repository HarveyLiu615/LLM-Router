using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using DesensitizeProxy.AspNetCore.Yarp;
using DesensitizeProxy.Core.Abstractions;
using DesensitizeProxy.Core.Models;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace DesensitizeProxy.AspNetCore.Middleware;

public sealed class DesensitizeProxyMiddleware : IMiddleware
{
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    private static readonly ForwarderRequestConfig ForwarderConfig = new()
    {
        ActivityTimeout = TimeSpan.FromMinutes(10)
    };

    private readonly IRuleEngine _ruleEngine;
    private readonly ILlmDesensitizer _llmDesensitizer;
    private readonly IPiiRedactor _piiRedactor;
    private readonly ISchemaCleaner _schemaCleaner;
    private readonly IHttpForwarder _forwarder;
    private readonly HttpMessageInvoker _httpClient;
    private readonly IOptionsMonitor<PrivacyConfig> _options;
    private readonly ILogger<DesensitizeProxyMiddleware> _logger;
    private readonly DesensitizeMetrics _metrics;
    private readonly UpstreamResolver _upstreamResolver;
    private readonly ProviderTransformerRegistry _providerTransformers;
    private readonly IRedactionAuditLogger _redactionAuditLogger;

    public DesensitizeProxyMiddleware(
        IRuleEngine ruleEngine,
        ILlmDesensitizer llmDesensitizer,
        IPiiRedactor piiRedactor,
        ISchemaCleaner schemaCleaner,
        IHttpForwarder forwarder,
        HttpMessageInvoker httpClient,
        IOptionsMonitor<PrivacyConfig> options,
        ILogger<DesensitizeProxyMiddleware> logger,
        DesensitizeMetrics metrics,
        UpstreamResolver upstreamResolver,
        ProviderTransformerRegistry providerTransformers,
        IRedactionAuditLogger redactionAuditLogger)
    {
        _ruleEngine = ruleEngine;
        _llmDesensitizer = llmDesensitizer;
        _piiRedactor = piiRedactor;
        _schemaCleaner = schemaCleaner;
        _forwarder = forwarder;
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
        _metrics = metrics;
        _upstreamResolver = upstreamResolver;
        _providerTransformers = providerTransformers;
        _redactionAuditLogger = redactionAuditLogger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var config = _options.CurrentValue;
        if (!config.Enabled || IsHealthRequest(context, config))
        {
            await next(context);
            return;
        }

        _metrics.AddRequest();
        if (context.Request.ContentLength > config.MaxBodySizeBytes)
        {
            await WriteRequestBodyTooLargeAsync(context, config.MaxBodySizeBytes);
            return;
        }

        if (!HasRequestBody(context.Request))
        {
            await ForwardWithoutBodyAsync(context, config);
            return;
        }

        string body;
        try
        {
            body = await ReadRequestBodyAsync(context.Request, config.MaxBodySizeBytes, context.RequestAborted);
        }
        catch (PayloadTooLargeException)
        {
            await WriteRequestBodyTooLargeAsync(context, config.MaxBodySizeBytes);
            return;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid json request body");
            return;
        }

        if (parsed is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "empty json request body");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var textParts = TextPartExtractor.Extract(parsed);
        var originalTexts = textParts
            .Where(part => !string.IsNullOrWhiteSpace(part.Text))
            .ToDictionary(part => part.Ref, part => part.Text);

        var regexHits = RunRegex(textParts, config);
        var queueResult = BuildLlmQueue(textParts, originalTexts, config);
        if (queueResult.BlockMessage is not null)
        {
            await WriteErrorAsync(context, queueResult.BlockStatusCode, queueResult.BlockMessage);
            return;
        }

        var llmQueue = queueResult.Queue;

        if (!await RunLlmAsync(llmQueue, config, context))
        {
            return;
        }

        UpstreamTarget target;
        try
        {
            target = _upstreamResolver.Resolve(context, parsed, config);
        }
        catch (InvalidOperationException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, ex.Message);
            return;
        }

        if (parsed["tools"] is JsonArray tools)
        {
            parsed["tools"] = _schemaCleaner.Clean(tools, target.Provider ?? "openai-compatible");
        }

        var providerTransformer = _providerTransformers.Resolve(target);
        var outboundBody = providerTransformer.TransformRequest(context.Request.Path, parsed, target).ToJsonString();
        ReplaceRequestBody(context, outboundBody);

        var transformer = new DesensitizeForwarderTransformer(parsed, target, providerTransformer);
        var error = await _forwarder.SendAsync(context, target.BaseUrl, _httpClient, ForwarderConfig, transformer);
        LogForwarderError(context, error);

        stopwatch.Stop();
        _logger.LogInformation(
            "Request desensitized: {Messages} parts, {RegexHits} regex hits, {LlmCalls} LLM calls, {TotalMs}ms total",
            textParts.Count,
            regexHits,
            llmQueue.Count,
            stopwatch.ElapsedMilliseconds);
    }

    private async Task ForwardWithoutBodyAsync(HttpContext context, PrivacyConfig config)
    {
        UpstreamTarget target;
        try
        {
            target = _upstreamResolver.Resolve(context, request: null, config);
        }
        catch (InvalidOperationException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, ex.Message);
            return;
        }

        var providerTransformer = _providerTransformers.Resolve(target);
        var transformer = new DesensitizeForwarderTransformer(requestBody: null, target, providerTransformer);
        var error = await _forwarder.SendAsync(context, target.BaseUrl, _httpClient, ForwarderConfig, transformer);
        LogForwarderError(context, error);
    }

    private void LogForwarderError(HttpContext context, ForwarderError error)
    {
        if (error == ForwarderError.None)
        {
            return;
        }

        var errorFeature = context.GetForwarderErrorFeature();
        if (IsCanceledForwarderError(error))
        {
            _logger.LogDebug(errorFeature?.Exception, "Forwarder canceled with {Error}", error);
            return;
        }

        _logger.LogWarning(errorFeature?.Exception, "Forwarder failed with {Error}", error);
    }

    private static bool IsCanceledForwarderError(ForwarderError error) => error switch
    {
        ForwarderError.RequestCanceled => true,
        ForwarderError.RequestBodyCanceled => true,
        ForwarderError.ResponseBodyCanceled => true,
        ForwarderError.UpgradeRequestCanceled => true,
        _ => false
    };

    private static bool HasRequestBody(HttpRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Headers.TransferEncoding))
        {
            return true;
        }

        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsDelete(request.Method))
        {
            return request.ContentLength > 0;
        }

        return request.ContentLength is null or > 0;
    }

    private int RunRegex(IReadOnlyList<TextPart> textParts, PrivacyConfig config)
    {
        var hits = 0;
        foreach (var part in textParts)
        {
            if (string.IsNullOrWhiteSpace(part.Text))
            {
                continue;
            }

            var original = part.Text;
            var redaction = part.IsSystem
                ? _piiRedactor.RedactSystemWithHits(original, config.SystemMessageRedaction)
                : _piiRedactor.RedactWithHits(original);
            if (redaction.HasHit)
            {
                hits++;
                foreach (var phase in redaction.HitPhases)
                {
                    _metrics.AddRegexHit(phase);
                }

                LogRedactionHits("regex", part, redaction.Hits, config);
                part.Text = redaction.Content;
                part.SetText(redaction.Content);
            }
        }

        return hits;
    }

    private LlmQueueResult BuildLlmQueue(
        IReadOnlyList<TextPart> textParts,
        IReadOnlyDictionary<TextPartRef, string> originalTexts,
        PrivacyConfig config)
    {
        var queue = new List<(TextPart Part, string OriginalText, DetectionResult Detection)>();
        foreach (var part in textParts)
        {
            if (string.IsNullOrWhiteSpace(part.Text) || !originalTexts.TryGetValue(part.Ref, out var original))
            {
                continue;
            }

            if (part.IsSystem && (!config.SystemMessageRedaction.RuleEngine || !config.SystemMessageRedaction.Llm))
            {
                continue;
            }

            if (part.IsSystem && config.SystemMessageRedaction.RuleEngine && !config.SystemMessageRedaction.Llm)
            {
                continue;
            }

            if (part.IsSystem is false || config.SystemMessageRedaction.RuleEngine)
            {
                var detection = _ruleEngine.Check(original);
                if (original.Length > config.MaxTextPartLengthForLlm)
                {
                    _logger.LogWarning("Text part too long for LLM desensitization: {Length} chars", original.Length);
                    if (config.StrictMode && detection.HitLevel != RuleHitLevel.None)
                    {
                        _metrics.AddStrictModeBlock();
                        return new LlmQueueResult(
                            queue,
                            StatusCodes.Status413PayloadTooLarge,
                            "PII semantic desensitization skipped because text part is too large");
                    }

                    continue;
                }

                if (ShouldRunLlm(detection, original, config))
                {
                    queue.Add((part, original, detection));
                }
            }
        }

        return new LlmQueueResult(queue, StatusCodes.Status200OK, null);
    }

    private bool ShouldRunLlm(DetectionResult detection, string original, PrivacyConfig config) =>
        detection.HitLevel == RuleHitLevel.Trigger ||
        detection.HitLevel == RuleHitLevel.Hint && (_piiRedactor.HasAnyHit(original) || config.StrictMode);

    private async Task<bool> RunLlmAsync(
        IReadOnlyList<(TextPart Part, string OriginalText, DetectionResult Detection)> llmQueue,
        PrivacyConfig config,
        HttpContext context)
    {
        if (llmQueue.Count == 0)
        {
            return true;
        }

        if (!config.LocalModel.Enabled)
        {
            if (config.StrictMode)
            {
                _metrics.AddStrictModeBlock();
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "PII semantic desensitization disabled in strict mode");
                return false;
            }

            _logger.LogWarning("Local model disabled, using regex-only desensitization");
            return true;
        }

        var items = llmQueue.Select(item => (item.OriginalText, item.Detection)).ToList();
        var started = Stopwatch.StartNew();
        var results = await _llmDesensitizer.DesensitizeBatchAsync(items, context.RequestAborted);
        started.Stop();
        _metrics.RecordLlmDuration(started.Elapsed.TotalSeconds);

        for (var i = 0; i < llmQueue.Count; i++)
        {
            var (part, original, detection) = llmQueue[i];
            var result = results[i];
            if (result.Status != DesensitizeStatus.Success)
            {
                _metrics.AddLlmFailure(result.FailureReason ?? result.Status.ToString().ToLowerInvariant());
                if (config.StrictMode && detection.HitLevel != RuleHitLevel.None)
                {
                    _metrics.AddStrictModeBlock();
                    await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "PII desensitization failed in strict mode");
                    return false;
                }

                _logger.LogWarning(
                    "LLM desensitize did not produce a successful result. Status: {Status}; falling back to regex-only. Content length: {Length}",
                    result.Status,
                    original.Length);
                continue;
            }

            LogRedactionHits(result.WasModelUsed ? "llm" : "llm-cache", part, result.RedactionHits, config);

            var finalRedaction = _piiRedactor.RedactWithHits(result.RedactedContent);
            if (finalRedaction.HasHit)
            {
                LogRedactionHits("llm-post-regex", part, finalRedaction.Hits, config);
            }

            part.Text = finalRedaction.Content;
            part.SetText(finalRedaction.Content);
            if (result.WasModelUsed)
            {
                _metrics.AddLlmCall();
            }
            else
            {
                _metrics.AddCacheHit();
            }
        }

        return true;
    }

    private void LogRedactionHits(
        string source,
        TextPart part,
        IReadOnlyList<RedactionHit> hits,
        PrivacyConfig config)
    {
        if (!config.Observability.RedactionLoggingEnabled || hits.Count == 0)
        {
            return;
        }

        var includeValues = config.Observability.RedactionLogIncludeValues;
        foreach (var hit in hits)
        {
            var originalValue = includeValues ? hit.OriginalValue : null;
            _redactionAuditLogger.Log(new RedactionAuditEntry(
                DateTimeOffset.UtcNow.ToOffset(BeijingOffset),
                source,
                part.Ref.Path,
                part.IsSystem,
                hit.Phase,
                hit.Label,
                hit.Count,
                originalValue,
                hit.RedactedValue));
        }
    }

    private static bool IsHealthRequest(HttpContext context, PrivacyConfig config) =>
        config.Observability.HealthCheckEnabled &&
        string.Equals(context.Request.Path, config.Observability.HealthCheckPath, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadRequestBodyAsync(HttpRequest request, long maxBytes, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        var buffer = new char[8192];
        var builder = new StringBuilder();
        long bytes = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
            if (bytes > maxBytes)
            {
                throw new PayloadTooLargeException();
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static void ReplaceRequestBody(HttpContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        context.Request.Headers.ContentLength = bytes.Length;
        context.Request.Headers.Remove("Transfer-Encoding");
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($"{{\"error\":{{\"message\":\"{message}\"}}}}");
    }

    private static Task WriteRequestBodyTooLargeAsync(HttpContext context, long maxBytes) =>
        WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, $"request body too large; max {maxBytes} bytes");

    private sealed class PayloadTooLargeException : Exception;

    private sealed record LlmQueueResult(
        List<(TextPart Part, string OriginalText, DetectionResult Detection)> Queue,
        int BlockStatusCode,
        string? BlockMessage);
}
