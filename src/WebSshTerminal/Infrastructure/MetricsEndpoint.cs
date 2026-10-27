using System.Security.Cryptography;
using System.Text;
using Prometheus;

namespace WebSshTerminal.Infrastructure;

/// <summary>
/// Servidor de mètriques en un port de GESTIÓ separat del trànsit d'usuari. El port no es publica al host
/// ni mitjançant Nginx/Traefik. Distingeix per port local del socket (no per capçaleres, que es poden falsificar).
/// </summary>
public static class MetricsEndpoint
{
    public static bool IsManagementRequest(HttpContext ctx, int managementPort) =>
        managementPort > 0 && ctx.Connection.LocalPort == managementPort;

    /// <summary>Només serveix GET /metrics; qualsevol altra cosa és 404. Amb token configurat exigeix Bearer.</summary>
    public static async Task HandleAsync(HttpContext ctx, string? token)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method) || ctx.Request.Path != "/metrics")
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!string.IsNullOrEmpty(token) && !IsAuthorized(ctx, token))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }
        ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    private static bool IsAuthorized(HttpContext ctx, string token)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var given = Encoding.UTF8.GetBytes(header[prefix.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }
}
