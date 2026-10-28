using System.Collections.Concurrent;

namespace WebSshTerminal.Infrastructure;

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, int EventId, string Message, string? Exception);

/// <summary>Filtre de la pantalla de logs. Tots els criteris són opcionals i es combinen amb AND.</summary>
public sealed record LogFilter(LogLevel MinLevel = LogLevel.Warning, string? Text = null, string? Category = null,
    int? EventId = null, TimeSpan? Window = null, int Limit = 200);

/// <summary>
/// Cercle en memòria amb les últimes entrades de log (les mateixes que va a Docker, ja sense secrets ni contingut).
/// Es perd en reiniciar; per a l'històric, <c>docker logs</c>. Només hi accedeixen els administradors.
/// </summary>
public sealed class LogBuffer
{
    public const int Capacity = 5000;
    private const int MaxExceptionChars = 4000;
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public int Count => _entries.Count;

    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry with { Exception = entry.Exception is { Length: > MaxExceptionChars } e ? e[..MaxExceptionChars] + "…" : entry.Exception });
        while (_entries.Count > Capacity && _entries.TryDequeue(out _)) { }
    }

    /// <summary>Entrades que compleixen el filtre, de la més recent a la més antiga.</summary>
    public IReadOnlyList<LogEntry> Query(LogFilter f, DateTimeOffset? now = null)
    {
        var since = f.Window is { } w ? (now ?? DateTimeOffset.Now) - w : (DateTimeOffset?)null;
        var limit = Math.Clamp(f.Limit, 1, 1000);
        var text = string.IsNullOrWhiteSpace(f.Text) ? null : f.Text.Trim();
        var cat = string.IsNullOrWhiteSpace(f.Category) ? null : f.Category.Trim();
        return [.. _entries.Reverse()
            .Where(e => e.Level >= f.MinLevel)
            .Where(e => since is null || e.Time >= since)
            .Where(e => f.EventId is null || e.EventId == f.EventId)
            .Where(e => cat is null || e.Category.Contains(cat, StringComparison.OrdinalIgnoreCase))
            .Where(e => text is null || e.Message.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || (e.Exception?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                        || e.Category.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(limit)];
    }
}

[ProviderAlias("Buffer")]
public sealed class BufferLoggerProvider(LogBuffer buffer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BufferLogger(categoryName, buffer);
    public void Dispose() { }

    private sealed class BufferLogger(string category, LogBuffer buffer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            buffer.Add(new LogEntry(DateTimeOffset.Now, logLevel, category, eventId.Id, formatter(state, exception), exception?.ToString()));
        }
    }
}
