using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WebSshTerminal.Tests;

/// <summary>Captura tot el que l'app registraria (missatge, estat estructurat i excepció) per cercar-hi valors canari.</summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => [.. _lines];
    public string All => string.Join('\n', _lines);

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, _lines);

    public void Dispose() { }

    private sealed class CaptureLogger(string category, ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var structured = state is IEnumerable<KeyValuePair<string, object?>> kv
                ? string.Join(' ', kv.Select(p => $"{p.Key}={p.Value}")) : "";
            sink.Enqueue($"{logLevel} {category} {eventId.Id} {formatter(state, exception)} {structured} {exception}");
        }
    }
}
