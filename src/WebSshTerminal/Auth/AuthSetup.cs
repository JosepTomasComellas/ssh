using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using WebSshTerminal.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WebSshTerminal.Configuration;
using WebSshTerminal.Data;

namespace WebSshTerminal.Auth;

public static class AuthSetup
{
    public const string MustChangeClaim = "must_change_pw";
    public const string AdminPolicy = "AdminOnly";
    public const string LoginLimiter = "login";

    public static IServiceCollection AddWebSshAuth(this IServiceCollection services, WebSshOptions options, AppSecrets secrets, bool isDevelopment)
    {
        Directory.CreateDirectory(options.DataDirectory);
        Directory.CreateDirectory(Path.Combine(options.DataDirectory, "keys"));

        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(options.DataDirectory, "webssh.db")}"));
        services.AddDataProtection()
            .SetApplicationName("web-ssh-terminal")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDirectory, "keys")));
        services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = new AppKeyXmlEncryptor(secrets));

        services.AddIdentityCore<AppUser>(o =>
            {
                o.Password.RequiredLength = 12;
                o.Password.RequireDigit = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireLowercase = false;
                o.User.RequireUniqueEmail = false;
                o.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._";
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders() // necessari per al reinici de contrasenya d'admin
            .AddClaimsPrincipalFactory<ClaimsFactory>();

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, c =>
            {
                c.Cookie.Name = "webssh.auth";
                c.Cookie.HttpOnly = true;
                c.Cookie.SameSite = SameSiteMode.Lax;
                c.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                c.ExpireTimeSpan = TimeSpan.FromHours(8);
                c.SlidingExpiration = false; // caducitat absoluta
                c.LoginPath = "/login";
                c.AccessDeniedPath = "/denied";
                c.Events.OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync;
            })
            // SecurityStampValidator també fa sign-out d'aquests esquemes; han d'existir.
            .AddCookie(IdentityConstants.TwoFactorRememberMeScheme, c => c.Cookie.Name = "webssh.2fa-remember")
            .AddCookie(IdentityConstants.TwoFactorUserIdScheme, c => c.Cookie.Name = "webssh.2fa-user");
        // AddIdentityCore no el registra; sense ell la revocació de sessions falla amb un 500.
        services.AddScoped<ISecurityStampValidator, SecurityStampValidator<AppUser>>();
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AdminPolicy, p => p.RequireRole(Roles.Admin));
        services.AddCascadingAuthenticationState();

        services.AddRateLimiter(r =>
        {
            r.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            r.AddPolicy(LoginLimiter, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.LoginRateLimitPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        services.AddHostedService<BootstrapService>();
        return services;
    }

    /// <summary>Afegeix la marca de «canvi de contrasenya pendent» a la cookie.</summary>
    private sealed class ClaimsFactory(UserManager<AppUser> um, RoleManager<IdentityRole> rm, Microsoft.Extensions.Options.IOptions<IdentityOptions> o)
        : UserClaimsPrincipalFactory<AppUser, IdentityRole>(um, rm, o)
    {
        protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
        {
            var id = await base.GenerateClaimsAsync(user);
            if (user.MustChangePassword) id.AddClaim(new Claim(MustChangeClaim, "1"));
            return id;
        }
    }
}
