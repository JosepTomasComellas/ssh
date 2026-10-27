using Microsoft.EntityFrameworkCore;
using WebSshTerminal.Configuration;
using WebSshTerminal.Data;
using WebSshTerminal.Ssh;

namespace WebSshTerminal.Terminal;

public enum CreateResult { Created, NoIdentity, LimitReached }

/// <summary>Totes les consultes filtren per propietari: un identificador aliè és indistingible d'un inexistent.</summary>
public sealed class TerminalSessionService(AppDbContext db, WebSshOptions options, SshConnector connector, AuditLog audit)
{
    public async Task<(CreateResult Result, TerminalSession? Session)> CreateAsync(AppUser user)
    {
        if (user.SshIdentityId is null) return (CreateResult.NoIdentity, null);

        var active = await db.TerminalSessions.CountAsync(s => s.OwnerUserId == user.Id && s.State == TerminalSessionState.Active);
        if (active >= options.MaxSessionsPerUser)
        {
            await audit.RecordAsync(AuditOps.SessionCreate, AuditResults.Limit, user.Id);
            return (CreateResult.LimitReached, null);
        }

        var session = new TerminalSession { OwnerUserId = user.Id, SshIdentityId = user.SshIdentityId.Value, TmuxName = TmuxNames.Generate() };
        db.TerminalSessions.Add(session);
        await db.SaveChangesAsync();
        await audit.RecordAsync(AuditOps.SessionCreate, AuditResults.Ok, user.Id, session.Id);
        return (CreateResult.Created, session);
    }

    public Task<List<TerminalSession>> ListAsync(string userId) =>
        db.TerminalSessions.Where(s => s.OwnerUserId == userId && s.State == TerminalSessionState.Active)
            .OrderBy(s => s.CreatedUtc).ToListAsync();

    public Task<TerminalSession?> GetOwnedAsync(string userId, Guid id) =>
        db.TerminalSessions.FirstOrDefaultAsync(s => s.Id == id && s.OwnerUserId == userId && s.State == TerminalSessionState.Active);

    public Task<SshIdentity?> GetIdentityAsync(Guid id) => db.SshIdentities.FirstOrDefaultAsync(i => i.Id == id);

    public async Task MarkClosedAsync(TerminalSession session, string actorId)
    {
        session.State = TerminalSessionState.Closed;
        await db.SaveChangesAsync();
        await audit.RecordAsync(AuditOps.SessionClose, AuditResults.Ok, actorId, session.Id);
    }

    public async Task TouchAsync(TerminalSession session)
    {
        session.LastSeenUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>Tanca la sessió tmux remota (si hi és) i la marca com a tancada.</summary>
    public async Task CloseAsync(TerminalSession session, string actorId, CancellationToken ct)
    {
        var identity = await GetIdentityAsync(session.SshIdentityId);
        if (identity is not null && TmuxNames.IsValid(session.TmuxName))
        {
            try
            {
                using var client = await connector.ConnectAsync(identity, ct);
                using var cmd = client.CreateCommand($"tmux kill-session -t {session.TmuxName}"); // nom validat: ^ws-[0-9a-f]{16}$
                await Task.Run(cmd.Execute, ct);
            }
            catch (SshConnectException)
            {
                // El servidor no és accessible: la sessió es tanca localment igualment.
            }
        }
        await MarkClosedAsync(session, actorId);
    }
}
