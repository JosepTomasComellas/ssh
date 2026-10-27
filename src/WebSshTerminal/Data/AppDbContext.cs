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
}

public static class Roles
{
    public const string Admin = "admin";
    public const string User = "user";
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options);

/// <summary>Només per a <c>dotnet ef</c>: no necessita arrencar l'aplicació ni els seus secrets.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
