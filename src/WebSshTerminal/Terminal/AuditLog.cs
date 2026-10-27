using WebSshTerminal.Data;

namespace WebSshTerminal.Terminal;

/// <summary>Operacions auditables: només metadades, mai contingut.</summary>
public static class AuditOps
{
    public const string SessionCreate = "session.create";
    public const string SessionAttach = "session.attach";
    public const string SessionDetach = "session.detach";
    public const string SessionClose = "session.close";
    public const string SshConnect = "ssh.connect";
    public const string SftpUpload = "sftp.upload";
    public const string SftpDownload = "sftp.download";
    public const string IdentityCreate = "identity.create";
    public const string IdentityAssign = "identity.assign";
    public const string IdentityDelete = "identity.delete";
}

public static class AuditResults
{
    public const string Ok = "ok";
    public const string Denied = "denied";
    public const string Failed = "failed";
    public const string HostKey = "hostkey-rejected";
    public const string Limit = "limit";
}

public sealed class AuditLog(AppDbContext db)
{
    /// <param name="amount">Dada agregada autoritzada (p. ex. bytes); mai contingut.</param>
    public async Task RecordAsync(string operation, string result, string? actorUserId, Guid? sessionId = null, long? amount = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            TimeUtc = DateTime.UtcNow,
            ActorUserId = actorUserId,
            SessionId = sessionId,
            Operation = operation,
            Result = result,
            Amount = amount,
        });
        await db.SaveChangesAsync();
    }
}
