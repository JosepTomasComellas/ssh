using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Renci.SshNet;

namespace WebSshTerminal.Terminal;

public enum BridgeEnd { ClientClosed, RemoteExit, Idle, TakenOver, SlowClient, Error }

/// <summary>
/// Pont WebSocket ↔ PTY. Trames binàries = entrada de teclat; trames de text = control JSON (resize).
/// El contingut mai es registra ni s'audita. Quan el client marxa, tmux continua viu al servidor remot.
/// </summary>
public static class TerminalBridge
{
    public const int MaxInputMessage = 16 * 1024;
    private const int MaxControlMessage = 1024;
    private const int MaxQueuedBytes = 4 * 1024 * 1024;

    public static async Task<BridgeEnd> RunAsync(WebSocket ws, SshClient client, string tmuxName, TimeSpan idleTimeout, CancellationToken ct)
    {
        using var shell = client.CreateShellStream("xterm-256color", 80, 24, 0, 0, 8192);
        var queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        long queued = 0;
        var remoteExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastActivity = Environment.TickCount64;

        shell.DataReceived += (_, e) =>
        {
            var data = e.Data;
            if (Interlocked.Add(ref queued, data.Length) > MaxQueuedBytes) { queue.Writer.TryComplete(); return; }
            queue.Writer.TryWrite(data);
        };
        shell.Closed += (_, _) => remoteExit.TrySetResult();
        shell.ErrorOccurred += (_, _) => remoteExit.TrySetResult();

        // tmuxName ve de TmuxNames.Generate() i s'ha validat: ^ws-[0-9a-f]{16}$
        shell.Write($"exec tmux new-session -A -s {tmuxName}\n");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var send = SendLoopAsync(ws, queue, n => Interlocked.Add(ref queued, -n), cts.Token);
        var receive = ReceiveLoopAsync(ws, shell, () => Interlocked.Exchange(ref lastActivity, Environment.TickCount64), cts.Token);
        var idle = IdleAsync(() => Interlocked.Read(ref lastActivity), idleTimeout, cts.Token);

        var first = await Task.WhenAny(send, receive, idle, remoteExit.Task);
        var end = first == remoteExit.Task ? BridgeEnd.RemoteExit
            : first == idle ? BridgeEnd.Idle
            : ct.IsCancellationRequested ? BridgeEnd.TakenOver
            : first == send ? BridgeEnd.SlowClient
            : BridgeEnd.ClientClosed;

        await cts.CancelAsync();
        try { await Task.WhenAll(send, receive, idle); }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { }

        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, end.ToString(), CancellationToken.None); }
            catch (WebSocketException) { /* el client ja no hi és */ }
        }
        return end;
    }

    private static async Task SendLoopAsync(WebSocket ws, Channel<byte[]> queue, Action<int> release, CancellationToken ct)
    {
        await foreach (var chunk in queue.Reader.ReadAllAsync(ct))
        {
            await ws.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, ct);
            release(chunk.Length);
        }
    }

    private static async Task ReceiveLoopAsync(WebSocket ws, ShellStream shell, Func<long> touch, CancellationToken ct)
    {
        var buffer = new byte[MaxInputMessage];
        while (!ct.IsCancellationRequested)
        {
            var length = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer.AsMemory(length), ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                length += r.Count;
                if (length >= buffer.Length && !r.EndOfMessage)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "too-big", CancellationToken.None);
                    return;
                }
            } while (!r.EndOfMessage);

            touch();
            if (r.MessageType == WebSocketMessageType.Binary)
            {
                shell.Write(buffer.AsSpan(0, length));
                shell.Flush();
            }
            else if (length <= MaxControlMessage)
            {
                TryResize(shell, buffer.AsSpan(0, length));
            }
        }
    }

    private static void TryResize(ShellStream shell, ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != "resize") return;
            var cols = Math.Clamp(root.GetProperty("cols").GetInt32(), 1, 500);
            var rows = Math.Clamp(root.GetProperty("rows").GetInt32(), 1, 200);
            shell.ChangeWindowSize((uint)cols, (uint)rows, 0, 0);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // Missatge de control mal format: s'ignora.
        }
    }

    private static async Task IdleAsync(Func<long> lastActivity, TimeSpan timeout, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var remaining = TimeSpan.FromMilliseconds(lastActivity()) + timeout - TimeSpan.FromMilliseconds(Environment.TickCount64);
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), ct);
        }
    }
}
