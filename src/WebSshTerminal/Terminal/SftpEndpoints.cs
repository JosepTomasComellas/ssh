using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;
using Microsoft.Net.Http.Headers;
using WebSshTerminal.Configuration;
using WebSshTerminal.Data;
using WebSshTerminal.Ssh;

namespace WebSshTerminal.Terminal;

public static class SftpEndpoints
{
    private const int MaxConcurrentPerUser = 2;
    private const int MaxListEntries = 1000;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    public static void MapSftpEndpoints(this WebApplication app)
    {
        var files = app.MapGroup("/api/files").WithApiGuard();

        files.MapGet("/list", (string? path, HttpContext ctx, UserManager<AppUser> um, TerminalSessionService sessions, SshConnector connector) =>
            WithSession(ctx, um, sessions, connector, async (s, _, _) =>
            {
                var dir = s.Resolve(path);
                var attrs = s.TryGet(dir);
                if (attrs is null || !attrs.IsDirectory) return Results.NotFound();
                var entries = s.Client.ListDirectory(dir)
                    .Where(e => e.Name is not ("." or ".."))
                    .Take(MaxListEntries)
                    .Select(e => new { name = e.Name, directory = e.IsDirectory, size = e.IsDirectory ? 0 : e.Length, modifiedUtc = e.LastWriteTimeUtc, link = e.IsSymbolicLink })
                    .OrderByDescending(e => e.directory).ThenBy(e => e.name, StringComparer.Ordinal);
                return await Task.FromResult(Results.Ok(entries));
            }));

        files.MapGet("/download", (string? path, HttpContext ctx, UserManager<AppUser> um, TerminalSessionService sessions, SshConnector connector, WebSshOptions options, AuditLog audit) =>
            WithSession(ctx, um, sessions, connector, async (s, userId, ct) =>
            {
                var file = s.Resolve(path);
                var attrs = s.TryGet(file);
                if (attrs is null || !attrs.IsRegularFile) return Results.NotFound();
                if (attrs.Length > options.MaxTransferBytes)
                {
                    await audit.RecordAsync(AuditOps.SftpDownload, AuditResults.Limit, userId);
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                var name = attrs.Name;
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength = attrs.Length;
                ctx.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = name }.ToString();
                long sent = 0;
                await using (var remote = s.Client.OpenRead(file))
                {
                    var buffer = new byte[64 * 1024];
                    int n;
                    while ((n = await remote.ReadAsync(buffer, ct)) > 0)
                    {
                        await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, n), ct);
                        sent += n;
                    }
                }
                await audit.RecordAsync(AuditOps.SftpDownload, AuditResults.Ok, userId, amount: sent);
                return Results.Empty;
            }));

        files.MapPost("/upload", (string? path, bool? overwrite, HttpContext ctx, UserManager<AppUser> um, TerminalSessionService sessions, SshConnector connector, WebSshOptions options, AuditLog audit) =>
            WithSession(ctx, um, sessions, connector, async (s, userId, ct) =>
            {
                if (ctx.Request.ContentLength is { } declared && declared > options.MaxTransferBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                var target = s.Resolve(path);
                if (SftpPaths.Normalize(path).Length == 0) throw new InvalidSftpPathException("no-filename");
                var existing = s.TryGet(target);
                if (existing is { IsDirectory: true }) return Results.Conflict(new { error = "is-directory" });
                if (existing is not null && overwrite != true) return Results.Conflict(new { error = "exists" });

                // Es puja a un fitxer temporal i només es mou al lloc final si la transferència és completa.
                var temp = target + ".upload-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
                long received = 0;
                try
                {
                    await using (var remote = s.Client.Create(temp))
                    {
                        var buffer = new byte[64 * 1024];
                        int n;
                        while ((n = await ctx.Request.Body.ReadAsync(buffer, ct)) > 0)
                        {
                            received += n;
                            if (received > options.MaxTransferBytes)
                            {
                                s.Client.DeleteFile(temp);
                                await audit.RecordAsync(AuditOps.SftpUpload, AuditResults.Limit, userId);
                                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                            }
                            await remote.WriteAsync(buffer.AsMemory(0, n), ct);
                        }
                    }
                    if (existing is not null) s.Client.DeleteFile(target);
                    s.Client.RenameFile(temp, target);
                }
                catch
                {
                    if (s.Client.Exists(temp)) s.Client.DeleteFile(temp);
                    throw;
                }
                await audit.RecordAsync(AuditOps.SftpUpload, AuditResults.Ok, userId, amount: received);
                return Results.StatusCode(StatusCodes.Status201Created);
            }));
    }

    /// <summary>Resol l'usuari, la seva identitat Linux assignada, limita concurrència i tradueix errors a respostes segures.</summary>
    private static async Task<IResult> WithSession(HttpContext ctx, UserManager<AppUser> um, TerminalSessionService sessions,
        SshConnector connector, Func<SftpSession, string, CancellationToken, Task<IResult>> action)
    {
        var user = await um.GetUserAsync(ctx.User);
        if (user?.SshIdentityId is null) return Results.Json(new { error = "no-identity" }, statusCode: StatusCodes.Status409Conflict);
        var identity = await sessions.GetIdentityAsync(user.SshIdentityId.Value);
        if (identity is null) return Results.Json(new { error = "no-identity" }, statusCode: StatusCodes.Status409Conflict);

        var gate = Gates.GetOrAdd(user.Id, _ => new SemaphoreSlim(MaxConcurrentPerUser));
        if (!await gate.WaitAsync(0)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        try
        {
            using var s = await SftpSession.OpenAsync(connector, identity, ctx.RequestAborted);
            return await action(s, user.Id, ctx.RequestAborted);
        }
        catch (InvalidSftpPathException)
        {
            return Results.Json(new { error = "invalid-path" }, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (SshConnectException)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (Renci.SshNet.Common.SftpPermissionDeniedException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            return Results.NotFound();
        }
        finally
        {
            gate.Release();
        }
    }
}
