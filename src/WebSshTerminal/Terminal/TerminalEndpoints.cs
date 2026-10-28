using System.Net.WebSockets;
using Microsoft.AspNetCore.Identity;
using WebSshTerminal.Configuration;
using WebSshTerminal.Data;
using WebSshTerminal.Infrastructure;
using WebSshTerminal.Ssh;

namespace WebSshTerminal.Terminal;

public static class OriginPolicy
{
    /// <summary>L'Origin ha d'existir i coincidir exactament amb l'origen públic configurat.</summary>
    public static bool IsAllowed(HttpRequest request, WebSshOptions options)
    {
        if (!request.Headers.TryGetValue("Origin", out var origin) || origin.Count != 1) return false;
        var allowed = new Uri(options.PublicUrl).GetLeftPart(UriPartial.Authority);
        return string.Equals(origin[0], allowed, StringComparison.OrdinalIgnoreCase);
    }
}

public static class TerminalEndpoints
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> DiagnoseCooldown = new();

    public static void MapTerminalEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/sessions").WithApiGuard();

        api.MapGet("", async (HttpContext ctx, UserManager<AppUser> um, TerminalSessionService sessions, LiveSessions live) =>
        {
            var userId = um.GetUserId(ctx.User)!;
            var list = await sessions.ListAsync(userId);
            return Results.Ok(list.Select(s => new { id = s.Id, createdUtc = s.CreatedUtc, lastSeenUtc = s.LastSeenUtc, attached = live.IsLive(s.Id) }));
        });

        api.MapPost("", async (HttpContext ctx, UserManager<AppUser> um, TerminalSessionService sessions) =>
        {
            var user = await um.GetUserAsync(ctx.User);
            if (user is null) return Results.Unauthorized();
            var (result, session) = await sessions.CreateAsync(user);
            return result switch
            {
                CreateResult.Created => Results.Json(new { id = session!.Id }, statusCode: StatusCodes.Status201Created),
                CreateResult.NoIdentity => Results.Json(new { error = "no-identity" }, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(new { error = "limit" }, statusCode: StatusCodes.Status429TooManyRequests),
            };
        });

        // Diagnosi: el navegador no veu per què falla l'obertura del WebSocket; aquí es prova la connexió SSH i es retorna
        // només una categoria segura (mai detalls del servidor ni de la credencial).
        api.MapPost("/{id:guid}/diagnose", async (Guid id, HttpContext ctx, UserManager<AppUser> um, TerminalSessionService sessions, SshConnector connector) =>
        {
            var userId = um.GetUserId(ctx.User)!;
            var now = DateTime.UtcNow;
            var last = DiagnoseCooldown.GetOrAdd(userId, DateTime.MinValue);
            if (now - last < TimeSpan.FromSeconds(3)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            DiagnoseCooldown[userId] = now;

            var session = await sessions.GetOwnedAsync(userId, id);
            if (session is null) return Results.NotFound();
            var identity = await sessions.GetIdentityAsync(session.SshIdentityId);
            if (identity is null) return Results.Json(new { ok = false, reason = "no-identity" });
            try
            {
                using var client = await connector.ConnectAsync(identity, ctx.RequestAborted);
                return Results.Json(new { ok = true, reason = "ok" });
            }
            catch (SshConnectException ex)
            {
                return Results.Json(new { ok = false, reason = ex.Failure switch { SshFailure.HostKeyRejected => "hostkey", SshFailure.AuthFailed => "authfailed", SshFailure.Unreachable => "unreachable", _ => "other" } });
            }
        });

        api.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, UserManager<AppUser> um, TerminalSessionService sessions, LiveSessions live) =>
        {
            var userId = um.GetUserId(ctx.User)!;
            var session = await sessions.GetOwnedAsync(userId, id);
            if (session is null) return Results.NotFound();
            await sessions.CloseAsync(session, userId, ctx.RequestAborted);
            return Results.NoContent();
        });

        app.Map("/ws/terminal/{id:guid}", async (Guid id, HttpContext ctx, UserManager<AppUser> um, WebSshOptions options,
            TerminalSessionService sessions, LiveSessions live, AuditLog audit, ILogger<LiveSessions> logger) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            if (!OriginPolicy.IsAllowed(ctx.Request, options))
            {
                AppMetrics.Rejections.WithLabels("origin").Inc();
                Log.WebSocketRejected(logger, "origin");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var userId = um.GetUserId(ctx.User)!;
            var session = await sessions.GetOwnedAsync(userId, id); // sessió aliena = inexistent
            if (session is null)
            {
                AppMetrics.Rejections.WithLabels("not-owner").Inc();
                Log.WebSocketRejected(logger, "not-owner");
                return Results.NotFound();
            }
            var identity = await sessions.GetIdentityAsync(session.SshIdentityId);
            if (identity is null) return Results.Problem(statusCode: StatusCodes.Status409Conflict);
            if (live.Count >= options.MaxTotalSessions && !live.IsLive(session.Id))
            {
                AppMetrics.Rejections.WithLabels("capacity").Inc();
                await audit.RecordAsync(AuditOps.SessionAttach, AuditResults.Limit, userId, session.Id);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            Renci.SshNet.SshClient client;
            try
            {
                client = await ctx.RequestServices.GetRequiredService<SshConnector>().ConnectAsync(identity, ctx.RequestAborted);
            }
            catch (SshConnectException ex)
            {
                await audit.RecordAsync(AuditOps.SshConnect, ex.Failure == SshFailure.HostKeyRejected ? AuditResults.HostKey : AuditResults.Failed, userId, session.Id);
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            using (client)
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                using var registration = live.Register(session.Id, cts);
                await audit.RecordAsync(AuditOps.SessionAttach, AuditResults.Ok, userId, session.Id);
                Log.TerminalAttached(logger, live.Count);
                AppMetrics.Attaches.WithLabels(session.LastSeenUtc - session.CreatedUtc > TimeSpan.FromSeconds(1) ? "resume" : "new").Inc();
                AppMetrics.ActiveSessions.Inc();
                var started = System.Diagnostics.Stopwatch.GetTimestamp();

                BridgeEnd end;
                try
                {
                    end = await TerminalBridge.RunAsync(ws, client, session.TmuxName, TimeSpan.FromMinutes(options.IdleTimeoutMinutes), cts.Token);
                }
                finally
                {
                    AppMetrics.ActiveSessions.Dec();
                    AppMetrics.SessionDuration.Observe(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
                }
                AppMetrics.Detaches.WithLabels(end.ToString().ToLowerInvariant()).Inc();

                await audit.RecordAsync(AuditOps.SessionDetach, AuditResults.Ok, userId, session.Id);
                if (end == BridgeEnd.RemoteExit) await sessions.MarkClosedAsync(session, userId); // tmux ha acabat
                else await sessions.TouchAsync(session);
                Log.TerminalDetached(logger, end.ToString(), live.Count);
            }
            return Results.Empty;
        });
    }
}
