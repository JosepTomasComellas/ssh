using Microsoft.AspNetCore.Mvc;
using WebSshTerminal.Terminal;
using Microsoft.AspNetCore.Identity;
using WebSshTerminal.Data;

namespace WebSshTerminal.Auth;

public static class AdminIdentityEndpoints
{
    public static void MapAdminIdentityEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/admin").RequireAuthorization(AuthSetup.AdminPolicy);

        admin.MapPost("/identities", async (
            [FromForm] string? name, [FromForm] string? linuxUser, [FromForm] string? privateKey, [FromForm] string? note,
            HttpContext ctx, UserManager<AppUser> um, SshIdentityService identities) =>
        {
            var actor = um.GetUserId(ctx.User)!;
            var (result, _) = await identities.CreateAsync(name ?? "", linuxUser ?? "", privateKey ?? "", note, actor);
            // La clau no es retorna mai: ni a la resposta ni a la redirecció.
            return Results.Redirect(result == IdentityResult2.Ok ? "/admin/identities?saved=1" : $"/admin/identities?error={result.ToString().ToLowerInvariant()}");
        });

        admin.MapPost("/identities/{id:guid}/delete", async (Guid id, HttpContext ctx, UserManager<AppUser> um, SshIdentityService identities) =>
        {
            var result = await identities.DeleteAsync(id, um.GetUserId(ctx.User)!);
            return Results.Redirect(result == IdentityResult2.Ok ? "/admin/identities" : $"/admin/identities?error={result.ToString().ToLowerInvariant()}");
        });

        admin.MapPost("/users/{userId}/identity", async (string userId, [FromForm] string? identityId, HttpContext ctx, UserManager<AppUser> um, SshIdentityService identities) =>
        {
            Guid? parsed = Guid.TryParse(identityId, out var g) ? g : null;
            var result = await identities.AssignAsync(userId, parsed, um.GetUserId(ctx.User)!);
            return Results.Redirect(result == IdentityResult2.Ok ? "/admin/users" : $"/admin/users?error={result.ToString().ToLowerInvariant()}");
        });
    }
}
