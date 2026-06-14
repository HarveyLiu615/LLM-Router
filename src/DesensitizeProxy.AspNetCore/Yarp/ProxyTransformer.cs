using System.Text.Json.Nodes;
using DesensitizeProxy.Core.Models;

namespace DesensitizeProxy.AspNetCore.Yarp;

public static class ProxyTransformer
{
    public static JsonNode TransformRequest(JsonNode request, UpstreamTarget target, IProviderTransformer transformer)
    {
        return transformer.TransformRequest(request, target);
    }
}
