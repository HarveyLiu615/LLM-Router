using Microsoft.Extensions.Options;

namespace DesensitizeProxy.Core.Tests;

internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = [];

    public TestOptionsMonitor(T value)
    {
        CurrentValue = value;
    }

    public T CurrentValue { get; private set; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new Subscription(_listeners, listener);
    }

    public void Set(T value, string? name = null)
    {
        CurrentValue = value;
        foreach (var listener in _listeners.ToArray())
        {
            listener(value, name);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly List<Action<T, string?>> _listeners;
        private readonly Action<T, string?> _listener;

        public Subscription(List<Action<T, string?>> listeners, Action<T, string?> listener)
        {
            _listeners = listeners;
            _listener = listener;
        }

        public void Dispose() => _listeners.Remove(_listener);
    }
}
