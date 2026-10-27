using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;
using WebSshTerminal.Data;
using WebSshTerminal.Infrastructure;

namespace WebSshTerminal.Terminal;

public enum IdentityResult2 { Ok, Invalid, Duplicate, NotFound, AlreadyAssigned }

/// <summary>Gestió d'identitats SSH (compte Linux + clau privada xifrada) i assignació explícita a usuaris web.</summary>
public sealed partial class SshIdentityService(AppDbContext db, UserManager<AppUser> users, CredentialProtector protector, AuditLog audit)
{
    public async Task<(IdentityResult2 Result, SshIdentity? Identity)> CreateAsync(
        string name, string linuxUser, string privateKeyPem, string? note, string actorId)
    {
        name = name.Trim();
        linuxUser = linuxUser.Trim();
        if (!NameRegex().IsMatch(name) || !LinuxUserRegex().IsMatch(linuxUser) || privateKeyPem.Length > 16 * 1024)
            return (IdentityResult2.Invalid, null);

        var keyBytes = Encoding.UTF8.GetBytes(privateKeyPem.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n");
        try
        {
            try
            {
                using var ms = new MemoryStream(keyBytes, writable: false);
                using var _ = new PrivateKeyFile(ms); // valida el format i que no estigui xifrada
            }
            catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ArgumentException or IOException or CryptographicException or FormatException or NotSupportedException)
            {
                return (IdentityResult2.Invalid, null);
            }

            if (await db.SshIdentities.AnyAsync(i => i.Name == name)) return (IdentityResult2.Duplicate, null);

            var identity = new SshIdentity { Name = name, LinuxUser = linuxUser, Note = note?.Trim() };
            identity.ProtectedKey = protector.Protect(keyBytes, identity.Id);
            db.SshIdentities.Add(identity);
            await db.SaveChangesAsync();
            await audit.RecordAsync(AuditOps.IdentityCreate, AuditResults.Ok, actorId);
            return (IdentityResult2.Ok, identity);
        }
        finally
        {
            Array.Clear(keyBytes);
        }
    }

    public async Task<IdentityResult2> AssignAsync(string userId, Guid? identityId, string actorId)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null) return IdentityResult2.NotFound;

        if (identityId is { } id)
        {
            if (!await db.SshIdentities.AnyAsync(i => i.Id == id)) return IdentityResult2.NotFound;
            // Una identitat Linux = un usuari web: evita que dos usuaris comparteixin shell i tmux.
            if (await db.Users.AnyAsync(u => u.SshIdentityId == id && u.Id != userId)) return IdentityResult2.AlreadyAssigned;
        }
        user.SshIdentityId = identityId;
        await users.UpdateAsync(user);
        await audit.RecordAsync(AuditOps.IdentityAssign, AuditResults.Ok, actorId);
        return IdentityResult2.Ok;
    }

    public async Task<IdentityResult2> DeleteAsync(Guid id, string actorId)
    {
        var identity = await db.SshIdentities.FindAsync(id);
        if (identity is null) return IdentityResult2.NotFound;
        if (await db.Users.AnyAsync(u => u.SshIdentityId == id)) return IdentityResult2.AlreadyAssigned;
        db.SshIdentities.Remove(identity);
        await db.SaveChangesAsync();
        await audit.RecordAsync(AuditOps.IdentityDelete, AuditResults.Ok, actorId);
        return IdentityResult2.Ok;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^[a-z_][a-z0-9_-]{0,31}$")]
    private static partial Regex LinuxUserRegex();
}
