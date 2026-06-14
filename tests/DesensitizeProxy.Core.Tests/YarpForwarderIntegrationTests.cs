using System.Net;
using System.Text;
using DesensitizeProxy.AspNetCore.Middleware;
using DesensitizeProxy.AspNetCore.Yarp;
using DesensitizeProxy.Core.Abstractions;
using DesensitizeProxy.Core.Engine;
using DesensitizeProxy.Core.Models;
using DesensitizeProxy.Core.Redaction;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Yarp.ReverseProxy.Forwarder;

namespace DesensitizeProxy.Core.Tests;

public sealed class YarpForwarderIntegrationTests
{
    [Fact]
    public async Task Middleware_UsesRealYarpForwarderWithoutReplacingOutgoingContent()
    {
        using var upstream = new LoopbackUpstream();
        await upstream.StartAsync();
        var config = new PrivacyConfig
        {
            LocalModel = { Enabled = false },
            Proxy = new ProxyConfig
            {
                Targets = new Dictionary<string, UpstreamTarget>
                {
                    ["default"] = new()
                    {
                        BaseUrl = upstream.BaseUrl,
                        ApiKey = "test-key",
                        Provider = "openai"
                    }
                }
            }
        };
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpForwarder();
        using var provider = services.BuildServiceProvider();
        var middleware = new DesensitizeProxyMiddleware(
            new RuleEngine(redactor, options),
            new NoopLlmDesensitizer(),
            redactor,
            new SchemaCleaner(),
            provider.GetRequiredService<IHttpForwarder>(),
            new HttpMessageInvoker(new SocketsHttpHandler()),
            options,
            NullLogger<DesensitizeProxyMiddleware>.Instance,
            new DesensitizeMetrics(),
            new UpstreamResolver(),
            new ProviderTransformerRegistry(),
            new NoopRedactionAuditLogger());
        var context = CreateJsonContext("/v1/responses", """
        {"model":"client-model","input":"电话13912345678"}
        """);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);
        var received = await upstream.ReceiveAsync();

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("/v1/responses", received.Path);
        Assert.Contains("\"model\":\"client-model\"", received.Body);
        Assert.Contains("[REDACTED:PHONE]", received.Body);
        Assert.DoesNotContain("13912345678", received.Body);
    }

    [Fact]
    public async Task Middleware_ForwardsModelsRequestThroughRealYarpWithoutRequestBody()
    {
        using var upstream = new LoopbackUpstream();
        await upstream.StartAsync();
        var config = new PrivacyConfig
        {
            LocalModel = { Enabled = false },
            Proxy = new ProxyConfig
            {
                Targets = new Dictionary<string, UpstreamTarget>
                {
                    ["default"] = new()
                    {
                        BaseUrl = upstream.BaseUrl,
                        ApiKey = "test-key",
                        Provider = "openai"
                    }
                }
            }
        };
        var middleware = CreateMiddleware(config);
        var context = CreateEmptyContext(HttpMethods.Get, "/v1/models");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);
        var received = await upstream.ReceiveAsync();

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("/v1/models", received.Path);
        Assert.Equal(string.Empty, received.Body);
    }

    private static DesensitizeProxyMiddleware CreateMiddleware(PrivacyConfig config)
    {
        var options = new TestOptionsMonitor<PrivacyConfig>(config);
        var redactor = new PiiRedactor(options);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpForwarder();
        var provider = services.BuildServiceProvider();
        return new DesensitizeProxyMiddleware(
            new RuleEngine(redactor, options),
            new NoopLlmDesensitizer(),
            redactor,
            new SchemaCleaner(),
            provider.GetRequiredService<IHttpForwarder>(),
            new HttpMessageInvoker(new SocketsHttpHandler()),
            options,
            NullLogger<DesensitizeProxyMiddleware>.Instance,
            new DesensitizeMetrics(),
            new UpstreamResolver(),
            new ProviderTransformerRegistry(),
            new NoopRedactionAuditLogger());
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

    private sealed class NoopLlmDesensitizer : ILlmDesensitizer
    {
        public Task<DesensitizeResult> DesensitizeAsync(string content, DetectionResult detection, CancellationToken cancellationToken) =>
            Task.FromResult(new DesensitizeResult(content, false, DesensitizeStatus.ModelFailure, "noop"));

        public Task<IReadOnlyList<DesensitizeResult>> DesensitizeBatchAsync(
            IReadOnlyList<(string Content, DetectionResult Detection)> items,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DesensitizeResult>>(items.Select(item => new DesensitizeResult(item.Content, false, DesensitizeStatus.ModelFailure, "noop")).ToList());
    }

    private sealed class NoopRedactionAuditLogger : IRedactionAuditLogger
    {
        public void Log(RedactionAuditEntry entry)
        {
        }
    }

    private sealed class LoopbackUpstream : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TaskCompletionSource<ReceivedRequest> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _cts;

        public string BaseUrl { get; private set; } = string.Empty;

        public Task StartAsync()
        {
            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _ = Task.Run(() => AcceptOneAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public async Task<ReceivedRequest> ReceiveAsync() =>
            await _received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Dispose()
        {
            _cts?.Cancel();
            _listener.Close();
            _cts?.Dispose();
        }

        private async Task AcceptOneAsync(CancellationToken cancellationToken)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync(cancellationToken);
                _received.TrySetResult(new ReceivedRequest(context.Request.RawUrl ?? string.Empty, body));

                var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                _received.TrySetException(ex);
            }
        }

        private static int GetFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    private sealed record ReceivedRequest(string Path, string Body);
}
