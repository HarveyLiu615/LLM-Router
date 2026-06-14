using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using DesensitizeProxy.AspNetCore.Auth;
using DesensitizeProxy.Core.Models;
using Yarp.ReverseProxy.Forwarder;

namespace DesensitizeProxy.AspNetCore.Yarp;

public sealed class DesensitizeForwarderTransformer : HttpTransformer
{
    private readonly JsonNode? _requestBody;
    private readonly UpstreamTarget _target;
    private readonly IProviderTransformer _providerTransformer;

    public DesensitizeForwarderTransformer(
        JsonNode? requestBody,
        UpstreamTarget target,
        IProviderTransformer providerTransformer)
    {
        _requestBody = requestBody;
        _target = target;
        _providerTransformer = providerTransformer;
    }

    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

        proxyRequest.Headers.Host = null;
        proxyRequest.RequestUri = BuildUri(httpContext, destinationPrefix);

        foreach (var header in MultiAuthHandler.ResolveAuthHeaders(_target))
        {
            proxyRequest.Headers.Remove(header.Key);
            if (!proxyRequest.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                if (proxyRequest.Content is not null)
                {
                    proxyRequest.Content.Headers.Remove(header.Key);
                    proxyRequest.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        proxyRequest.Headers.Accept.Clear();
        foreach (var accept in httpContext.Request.Headers.Accept)
        {
            proxyRequest.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(accept!));
        }
    }

    public override ValueTask<bool> TransformResponseAsync(
        HttpContext httpContext,
        HttpResponseMessage? proxyResponse,
        CancellationToken cancellationToken)
    {
        if (proxyResponse is not null)
        {
            _providerTransformer.ApplyResponseHeaders(proxyResponse, _target);
        }

        return base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
    }

    private Uri BuildUri(HttpContext httpContext, string destinationPrefix)
    {
        var pathAndQuery = _providerTransformer.BuildPath(httpContext.Request.Path, httpContext.Request.QueryString, _requestBody, _target);
        var baseUri = new Uri(destinationPrefix.TrimEnd('/') + "/", UriKind.Absolute);
        return new Uri(baseUri, pathAndQuery.TrimStart('/'));
    }
}
