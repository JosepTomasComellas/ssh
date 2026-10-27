using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebSshTerminal.Data;
using WebSshTerminal.Infrastructure;

namespace WebSshTerminal.Auth;

/// <summary>Aplica migracions i crea el primer admin NOMÉS si la base de dades no té cap usuari.</summary>
public sealed class BootstrapService(IServiceProvider services, AppSecrets secrets, ILogger<BootstrapService> logger) : IHostedService
{
    public const string AdminUserName = "admin";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(cancellationToken);

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var r in new[] { Roles.Admin, Roles.User })
            if (!await roles.RoleExistsAsync(r)) await roles.CreateAsync(new IdentityRole(r));

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (await users.Users.AnyAsync(cancellationToken)) return;

        var admin = new AppUser { UserName = AdminUserName, MustChangePassword = true, LockoutEnabled = true };
        var created = await users.CreateAsync(admin, secrets.BootstrapPassword);
        if (!created.Succeeded)
        {
            // Els codis d'error de política no contenen la contrasenya.
            Log.BootstrapFailed(logger, string.Join(',', created.Errors.Select(e => e.Code)));
            throw new InvalidOperationException("No s'ha pogut crear l'administrador inicial.");
        }
        await users.AddToRoleAsync(admin, Roles.Admin);
        Log.BootstrapCreated(logger);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
