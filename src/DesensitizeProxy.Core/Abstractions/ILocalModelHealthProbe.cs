namespace DesensitizeProxy.Core.Abstractions;

public interface ILocalModelHealthProbe
{
    Task<string> CheckAsync(CancellationToken cancellationToken);
}
