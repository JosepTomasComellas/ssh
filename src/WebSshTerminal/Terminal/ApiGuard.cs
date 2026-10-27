using WebSshTerminal.Configuration;

namespace WebSshTerminal.Terminal;

public static class ApiGuard
{
    /// <summary>Mutacions només amb Origin propi i capçalera personalitzada: impedeix CSRF entre orígens.</summary>
    public static RouteGroupBuilder WithApiGuard(this RouteGroupBuilder group) => group.AddEndpointFilter(async (ctx, next) =>
    {
        var req = ctx.HttpContext.Request;
        if (!HttpMethods.IsGet(req.Method))
        {
            var options = ctx.HttpContext.RequestServices.GetRequiredService<WebSshOptions>();
            if (!OriginPolicy.IsAllowed(req, options) || req.Headers["X-Requested-With"] != "WebSsh")
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        return await next(ctx);
    });
}
