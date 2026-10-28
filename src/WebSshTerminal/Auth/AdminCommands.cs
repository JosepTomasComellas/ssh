using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebSshTerminal.Data;
using WebSshTerminal.Terminal;

namespace WebSshTerminal.Auth;

/// <summary>
/// Ordres d'administració de línia d'ordres (s'executen dins el contenidor amb la mateixa configuració).
/// <c>WebSshTerminal --create-admin correu@domini.cat [--mfa-grace-days 7]</c>
/// </summary>
public static partial class AdminCommands
{
    public const int DefaultGraceDays = 7;
    public const int MaxGraceDays = 30;
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789"; // sense caràcters ambigus

    public sealed record CreateResult(bool Ok, string Message, string? Password = null, DateTime? MfaGraceUntilUtc = null);

    /// <summary>Crea un administrador amb una contrasenya aleatòria d'un sol ús. Mai sobreescriu un usuari existent.</summary>
    public static async Task<CreateResult> CreateAdminAsync(IServiceProvider services, string email, int graceDays, CancellationToken ct = default)
    {
        email = email.Trim();
        if (email.Length > 254 || !EmailRegex().IsMatch(email))
            return new(false, "Correu no vàlid.");
        if (graceDays is < 0 or > MaxGraceDays)
            return new(false, $"--mfa-grace-days ha d'estar entre 0 i {MaxGraceDays}.");

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(ct);

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var r in new[] { Roles.Admin, Roles.User })
            if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole(r));

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (await users.FindByNameAsync(email) is not null)
            return new(false, "Aquest usuari ja existeix. Usa «Reinicia contrasenya» a Admin → Usuaris.");

        var password = RandomNumberGenerator.GetString(PasswordAlphabet, 24);
        DateTime? grace = graceDays > 0 ? DateTime.UtcNow.AddDays(graceDays) : null;
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            LockoutEnabled = true,
            MustChangePassword = true,
            MfaGraceUntilUtc = grace,
        };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
            return new(false, "No s'ha pogut crear l'usuari: " + string.Join(", ", created.Errors.Select(e => e.Code)));
        await users.AddToRoleAsync(user, Roles.Admin);
        await scope.ServiceProvider.GetRequiredService<AuditLog>().RecordAsync(AuditOps.AdminCreate, AuditResults.Ok, actorUserId: null);
        return new(true, "Administrador creat.", password, grace);
    }

    /// <summary>Punt d'entrada de <c>--create-admin</c>: la contrasenya només s'imprimeix a stdout, mai als logs.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var i = Array.IndexOf(args, "--create-admin");
        if (i < 0 || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync("Ús: WebSshTerminal --create-admin correu@domini.cat [--mfa-grace-days 7]");
            return 2;
        }
        var graceDays = DefaultGraceDays;
        var g = Array.IndexOf(args, "--mfa-grace-days");
        if (g >= 0 && (g + 1 >= args.Length || !int.TryParse(args[g + 1], out graceDays)))
        {
            await Console.Error.WriteLineAsync("--mfa-grace-days necessita un nombre enter.");
            return 2;
        }

        var result = await CreateAdminAsync(services, args[i + 1], graceDays);
        if (!result.Ok)
        {
            await Console.Error.WriteLineAsync("ERROR: " + result.Message);
            return 1;
        }
        Console.WriteLine($"Administrador creat: {args[i + 1].Trim()}");
        Console.WriteLine($"Contrasenya inicial (només es mostra ara): {result.Password}");
        Console.WriteLine("Cal canviar-la en el primer accés.");
        Console.WriteLine(result.MfaGraceUntilUtc is { } until
            ? $"MFA no obligatori fins al {until.ToLocalTime():dd/MM/yyyy HH:mm}; després l'app el demanarà."
            : "El MFA és obligatori des del primer accés.");
        return 0;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}$")]
    private static partial Regex EmailRegex();
}
