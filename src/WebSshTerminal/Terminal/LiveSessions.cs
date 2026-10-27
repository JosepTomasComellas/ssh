using System.Collections.Concurrent;

namespace WebSshTerminal.Terminal;

/// <summary>Pontets WebSocket vius. Un segon accés a la mateixa sessió substitueix el primer (refresh/reconnexió).</summary>
public sealed class LiveSessions
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _live = new();

    public int Count => _live.Count;
    public bool IsLive(Guid id) => _live.ContainsKey(id);

    public IDisposable Register(Guid sessionId, CancellationTokenSource cts)
    {
        _live.AddOrUpdate(sessionId, cts, (_, previous) =>
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { /* ja tancat */ }
            return cts;
        });
        return new Handle(this, sessionId, cts);
    }

    public void CancelAll()
    {
        foreach (var cts in _live.Values)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* ja tancat */ }
        }
    }

    private sealed class Handle(LiveSessions owner, Guid id, CancellationTokenSource cts) : IDisposable
    {
        public void Dispose() => owner._live.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(id, cts));
    }
}
