namespace DesensitizeProxy.AspNetCore.Middleware;

public sealed record TextPartRef(string Path);

public sealed class TextPart
{
    public required TextPartRef Ref { get; init; }
    public required string Text { get; set; }
    public required bool IsSystem { get; init; }
    public required Action<string> SetText { get; init; }
}
