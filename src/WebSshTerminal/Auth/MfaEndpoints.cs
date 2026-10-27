using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using WebSshTerminal.Configuration;
using WebSshTerminal.Data;
using WebSshTerminal.Infrastructure;

namespace WebSshTerminal.Auth;

/// <summary>TOTP opcional (RFC 6238) amb codis de recuperació d'un sol ús. La clau no es registra mai.</summary>
public static class MfaEndpoints
{
    public static string OtpAuthUri(string issuer, string user, string key) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(user)}?secret={key}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";

    public static void MapMfaEndpoints(this WebApplication app)
    {
        var account = app.MapGroup("/account");

        // Segon pas del login: ja hi ha la cookie temporal de «contrasenya correcta».
        account.MapPost("/2fa", async ([FromForm] string? code, [FromForm] string? recovery, SignInManager<AppUser> signIn) =>
        {
            var clean = (code ?? "").Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
            var result = !string.IsNullOrEmpty(recovery)
                ? await signIn.TwoFactorRecoveryCodeSignInAsync(recovery.Trim())
                : await signIn.TwoFactorAuthenticatorSignInAsync(clean, isPersistent: false, rememberClient: false);
            AppMetrics.Logins.WithLabels(result.Succeeded ? "ok" : result.IsLockedOut ? "locked" : "failed").Inc();
            return Results.Redirect(result.Succeeded ? "/" : result.IsLockedOut ? "/login?error=1" : "/login/2fa?error=1");
        }).AllowAnonymous().RequireRateLimiting(AuthSetup.LoginLimiter);

        account.MapPost("/mfa/start", async (HttpContext ctx, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.GetUserAsync(ctx.User);
            if (user is null || user.TwoFactorEnabled) return Results.Redirect("/account/mfa");
            await users.ResetAuthenticatorKeyAsync(user); // nova clau; encara no activa
            await signIn.RefreshSignInAsync(user);       // la clau renova el security stamp: no expulsar l'usuari
            return Results.Redirect("/account/mfa");
        });

        account.MapPost("/mfa/confirm", async ([FromForm] string? code, HttpContext ctx, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var user = await users.GetUserAsync(ctx.User);
            if (user is null || user.TwoFactorEnabled) return Results.Redirect("/account/mfa");
            var clean = (code ?? "").Replace(" ", "", StringComparison.Ordinal);
            if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, clean))
                return Results.Redirect("/account/mfa?error=code");

            await users.SetTwoFactorEnabledAsync(user, true);
            var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10) ?? [];
            await users.UpdateSecurityStampAsync(user);
            await signIn.RefreshSignInAsync(user);
            // Els codis es mostren una sola vegada, a la resposta; no es desen ni es registren en clar.
            var items = string.Concat(codes.Select(c => $"<li><code>{WebUtility.HtmlEncode(c)}</code></li>"));
            return Results.Content(
                $"""
                <!doctype html><html lang="ca"><meta charset="utf-8"><title>Codis de recuperació</title>
                <body style="font-family:sans-serif;max-width:40rem;margin:2rem auto">
                <h1>MFA activat</h1>
                <p>Desa aquests codis de recuperació en un lloc segur. Cadascun serveix <b>una sola vegada</b> si perds l'aplicació d'autenticació. No es tornaran a mostrar.</p>
                <ul id="recovery-codes">{items}</ul>
                <p><a href="/account/mfa">Continua</a></p></body></html>
                """, "text/html; charset=utf-8");
        });

        account.MapPost("/mfa/disable", async ([FromForm] string? password, HttpContext ctx, UserManager<AppUser> users, SignInManager<AppUser> signIn, WebSshOptions options) =>
        {
            var user = await users.GetUserAsync(ctx.User);
            if (user is null || !user.TwoFactorEnabled) return Results.Redirect("/account/mfa");
            if (options.RequireMfa) return Results.Redirect("/account/mfa?error=required"); // política: no es pot desactivar
            if (string.IsNullOrEmpty(password) || !await users.CheckPasswordAsync(user, password))
                return Results.Redirect("/account/mfa?error=password");
            await users.SetTwoFactorEnabledAsync(user, false);
            await users.ResetAuthenticatorKeyAsync(user);
            await users.UpdateSecurityStampAsync(user);
            await signIn.RefreshSignInAsync(user);
            return Results.Redirect("/account/mfa");
        });
    }
}
