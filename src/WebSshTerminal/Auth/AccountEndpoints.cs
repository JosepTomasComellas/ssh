using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using WebSshTerminal.Data;
using WebSshTerminal.Infrastructure;

namespace WebSshTerminal.Auth;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var account = app.MapGroup("/account");

        account.MapPost("/login", async (
            [FromForm] string? username, [FromForm] string? password, [FromForm] string? returnUrl,
            SignInManager<AppUser> signIn, UserManager<AppUser> users, ILogger<AppUser> logger) =>
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return Results.Redirect("/login?error=1");

            var user = await users.FindByNameAsync(username);
            if (user is null)
            {
                // Iguala el cost de verificació per no revelar si el compte existeix.
                new PasswordHasher<AppUser>().HashPassword(new AppUser(), password);
                return Results.Redirect("/login?error=1");
            }

            var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
            if (result.IsLockedOut) Log.LockedOut(logger);
            if (!result.Succeeded) return Results.Redirect("/login?error=1");

            if (user.MustChangePassword) return Results.Redirect("/password");
            return Results.LocalRedirect(IsSafeLocal(returnUrl) ? returnUrl! : "/");
        }).AllowAnonymous().RequireRateLimiting(AuthSetup.LoginLimiter);

        account.MapPost("/logout", async (SignInManager<AppUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.Redirect("/login");
        });

        account.MapPost("/password", async (
            [FromForm] string? current, [FromForm] string? next, [FromForm] string? confirm,
            HttpContext ctx, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.GetUserAsync(ctx.User);
            if (user is null) return Results.Redirect("/login");
            if (next != confirm || string.IsNullOrEmpty(next) || string.IsNullOrEmpty(current))
                return Results.Redirect("/password?error=mismatch");

            var res = await users.ChangePasswordAsync(user, current, next);
            if (!res.Succeeded) return Results.Redirect("/password?error=policy");

            user.MustChangePassword = false;
            await users.UpdateAsync(user);
            await signIn.RefreshSignInAsync(user); // nova cookie amb el stamp actualitzat i sense la marca
            return Results.Redirect("/");
        });

        var admin = app.MapGroup("/admin").RequireAuthorization(AuthSetup.AdminPolicy);

        admin.MapPost("/users", async (
            [FromForm] string? username, [FromForm] string? role, UserManager<AppUser> users, ILogger<AppUser> logger) =>
        {
            if (string.IsNullOrWhiteSpace(username)) return Results.Redirect("/admin/users?error=invalid");
            var wanted = role == Roles.Admin ? Roles.Admin : Roles.User;
            var temp = NewTemporaryPassword();
            var user = new AppUser { UserName = username.Trim(), MustChangePassword = true, LockoutEnabled = true };
            var res = await users.CreateAsync(user, temp);
            if (!res.Succeeded) return Results.Redirect("/admin/users?error=invalid");
            await users.AddToRoleAsync(user, wanted);
            Log.AdminAction(logger, "user-create");
            return TempPasswordPage(user.UserName!, temp);
        });

        admin.MapPost("/users/{id}/reset", async (string id, UserManager<AppUser> users, ILogger<AppUser> logger) =>
        {
            var user = await users.FindByIdAsync(id);
            if (user is null) return Results.NotFound();
            var temp = NewTemporaryPassword();
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var res = await users.ResetPasswordAsync(user, token, temp);
            if (!res.Succeeded) return Results.Redirect("/admin/users?error=invalid");
            user.MustChangePassword = true;
            await users.UpdateAsync(user); // també invalida les sessions (security stamp)
            Log.AdminAction(logger, "password-reset");
            return TempPasswordPage(user.UserName!, temp);
        });

        admin.MapPost("/users/{id}/revoke", async (string id, UserManager<AppUser> users, ILogger<AppUser> logger) =>
        {
            var user = await users.FindByIdAsync(id);
            if (user is null) return Results.NotFound();
            await users.UpdateSecurityStampAsync(user);
            Log.AdminAction(logger, "sessions-revoke");
            return Results.Redirect("/admin/users");
        });

        admin.MapPost("/users/{id}/disable", async (string id, [FromForm] bool disable, HttpContext ctx, UserManager<AppUser> users, ILogger<AppUser> logger) =>
        {
            var user = await users.FindByIdAsync(id);
            if (user is null) return Results.NotFound();
            if (user.Id == users.GetUserId(ctx.User)) return Results.Redirect("/admin/users?error=self");
            await users.SetLockoutEndDateAsync(user, disable ? DateTimeOffset.MaxValue : null);
            await users.UpdateSecurityStampAsync(user);
            Log.AdminAction(logger, disable ? "user-disable" : "user-enable");
            return Results.Redirect("/admin/users");
        });
    }

    /// <summary>La contrasenya temporal es mostra una sola vegada a la resposta; no es desa ni es registra.</summary>
    private static IResult TempPasswordPage(string username, string temp) => Results.Content(
        $"""
        <!doctype html><html lang="ca"><meta charset="utf-8"><title>Contrasenya temporal</title>
        <body style="font-family:sans-serif;max-width:40rem;margin:2rem auto">
        <h1>Contrasenya temporal</h1>
        <p>Usuari: <b>{System.Net.WebUtility.HtmlEncode(username)}</b></p>
        <p><code id="temp">{System.Net.WebUtility.HtmlEncode(temp)}</code></p>
        <p>Només es mostra ara. L'usuari haurà de canviar-la en entrar.</p>
        <p><a href="/admin/users">Torna a usuaris</a></p></body></html>
        """, "text/html; charset=utf-8");

    internal static bool IsSafeLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));

    internal static string NewTemporaryPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', 'A').Replace('/', 'B');
}
