using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WebSshTerminal.Data;

public sealed class AppUser : IdentityUser
{
    /// <summary>Obliga a canviar la contrasenya abans d'usar res més (alta, bootstrap o reset d'admin).</summary>
    public bool MustChangePassword { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Identitat SSH (compte Linux + credencial) assignada explícitament per un administrador.</summary>
    public Guid? SshIdentityId { get; set; }

    /// <summary>Si és futur, aquest usuari no queda bloquejat per la política RequireMfa fins a aquesta data (UTC).</summary>
    public DateTime? MfaGraceUntilUtc { get; set; }
}

/// <summary>Compte Linux remot i la seva clau privada, xifrada en repòs amb la clau mestra de l'app.</summary>
public sealed class SshIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string LinuxUser { get; set; } = "";
    /// <summary>Permet sudo remot? És només informatiu: la política real viu al servidor remot.</summary>
    public string? Note { get; set; }
    public byte[] ProtectedKey { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum TerminalSessionState { Active, Closed }

/// <summary>Sessió de terminal d'un usuari web. El nom de tmux es genera al servidor, mai ve del client.</summary>
public sealed class TerminalSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerUserId { get; set; } = "";
    public Guid SshIdentityId { get; set; }
    public string TmuxName { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public TerminalSessionState State { get; set; } = TerminalSessionState.Active;
}

/// <summary>Només metadades: mai ordres, sortida de terminal, noms de fitxer ni secrets.</summary>
public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public string? ActorUserId { get; set; }
    public Guid? SessionId { get; set; }
    public string Operation { get; set; } = "";
    public string Result { get; set; } = "";
    /// <summary>Dada agregada autoritzada (p. ex. bytes), mai contingut.</summary>
    public long? Amount { get; set; }
}

public static class Roles
{
    public const string Admin = "admin";
    public const string User = "user";
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<SshIdentity> SshIdentities => Set<SshIdentity>();
    public DbSet<TerminalSession> TerminalSessions => Set<TerminalSession>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<SshIdentity>().HasIndex(i => i.Name).IsUnique();
        builder.Entity<SshIdentity>().Property(i => i.Name).HasMaxLength(64);
        builder.Entity<SshIdentity>().Property(i => i.LinuxUser).HasMaxLength(32);
        builder.Entity<TerminalSession>().HasIndex(s => new { s.OwnerUserId, s.State });
        builder.Entity<TerminalSession>().Property(s => s.TmuxName).HasMaxLength(32);
        builder.Entity<AuditEvent>().HasIndex(a => a.TimeUtc);
        builder.Entity<AuditEvent>().Property(a => a.Operation).HasMaxLength(48);
        builder.Entity<AuditEvent>().Property(a => a.Result).HasMaxLength(24);
    }
}

/// <summary>Només per a <c>dotnet ef</c>: no necessita arrencar l'aplicació ni els seus secrets.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
