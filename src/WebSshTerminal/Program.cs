using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using MudBlazor.Services;
using Prometheus;
using WebSshTerminal.Auth;
using WebSshTerminal.Components;
using WebSshTerminal.Configuration;
using WebSshTerminal.Infrastructure;
using WebSshTerminal.Ssh;
using WebSshTerminal.Terminal;

if (args.Contains("--healthcheck")) return await HealthProbe.RunAsync();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    // Hora local de la instància (zona via TZ), amb offset explícit.
    o.UseUtcTimestamp = false;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:sszzz ";
    o.IncludeScopes = false;
});

var options = builder.Configuration.GetSection(WebSshOptions.Section).Get<WebSshOptions>() ?? new WebSshOptions();
var problems = new List<string>(WebSshOptionsValidator.Validate(options, requireHttps: !builder.Environment.IsDevelopment()));
var secrets = AppSecrets.Load(options, problems);
if (problems.Count > 0)
{
    // Falla tancada: només es mostren noms de camp, mai valors.
    using var bootLog = LoggerFactory.Create(l => l.AddJsonConsole(o =>
    {
        o.UseUtcTimestamp = false;
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:sszzz ";
    }));
    var logger = bootLog.CreateLogger("Startup");
    foreach (var p in problems) Log.InvalidConfiguration(logger, p);
    return 78; // EX_CONFIG
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(secrets);
builder.Services.AddSingleton<BrandingService>();
builder.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromSeconds(options.ShutdownTimeoutSeconds));

// Confiança de proxys: només IP exactes configurades; mai confiança global.
builder.Services.Configure<ForwardedHeadersOptions>(f =>
{
    f.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    f.ForwardLimit = 1;
    f.KnownProxies.Clear();
    f.KnownIPNetworks.Clear();
    foreach (IPAddress ip in WebSshOptionsValidator.ParseProxies(options.TrustedProxies)) f.KnownProxies.Add(ip);
});

builder.Services.AddWebSshAuth(options, secrets, builder.Environment.IsDevelopment());
if (!builder.Environment.IsDevelopment())
{
    var publicHost = new Uri(options.PublicUrl).Host;
    builder.Services.Configure<Microsoft.AspNetCore.HostFiltering.HostFilteringOptions>(h => h.AllowedHosts = [publicHost]);
}
builder.Services.AddSingleton<CredentialProtector>();
builder.Services.AddSingleton<SshConnector>();
builder.Services.AddSingleton<LiveSessions>();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<TerminalSessionService>();
builder.Services.AddScoped<SshIdentityService>();
builder.Services.AddMudServices();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

// Port de gestió: només /metrics, abans de qualsevol altre middleware.
app.MapWhen(c => MetricsEndpoint.IsManagementRequest(c, options.MetricsPort), b => b.Run(ctx => MetricsEndpoint.HandleAsync(ctx, secrets.MetricsToken)));

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseHttpMetrics(o => o.ReduceStatusCodeCardinality());
// Branding de la instància (logo/fons) des del volum de dades; públic perquè la pantalla d'accés l'ha de mostrar.
var brandingDir = new BrandingService(options).InstanceDirectory;
Directory.CreateDirectory(brandingDir);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(brandingDir),
    RequestPath = BrandingService.InstanceRequestPath,
});

app.UseRouting(); // explícit: rate limiter, autorització i antiforgery necessiten l'endpoint ja resolt

// Les pàgines d'estat només per a la UI: re-executar /not-found amb DELETE/POST de l'API
// deformaria la resposta (405/400 en lloc del 404/403 real).
app.UseWhen(
    c => (HttpMethods.IsGet(c.Request.Method) || HttpMethods.IsHead(c.Request.Method))
        && !c.Request.Path.StartsWithSegments("/api") && !c.Request.Path.StartsWithSegments("/ws"),
    b => b.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/admin") || path.StartsWithSegments("/account") || path.StartsWithSegments("/login") || path.StartsWithSegments("/password"))
        h.CacheControl = "no-store";
    await next();
});

var wsOptions = new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) };
wsOptions.AllowedOrigins.Add(new Uri(options.PublicUrl).GetLeftPart(UriPartial.Authority));
app.UseWebSockets(wsOptions);

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Mentre hi hagi un canvi de contrasenya pendent, només es pot accedir a les pàgines per fer-lo.
app.Use(async (ctx, next) =>
{
    if (ctx.User.HasClaim(AuthSetup.MustChangeClaim, "1") && !IsAllowedWhileChangingPassword(ctx.Request.Path))
    {
        ctx.Response.Redirect("/password");
        return;
    }
    await next();
});

// Política RequireMfa: fins que l'usuari activi MFA només pot accedir a la pàgina per fer-ho.
app.Use(async (ctx, next) =>
{
    if (ctx.User.HasClaim(AuthSetup.MfaPendingClaim, "1") && !IsAllowedWhileChangingPassword(ctx.Request.Path))
    {
        ctx.Response.Redirect("/account/mfa");
        return;
    }
    await next();
});

app.UseAntiforgery();

app.MapGet("/health/live", () => Results.Text("""{"status":"ok"}""", "application/json")).AllowAnonymous();
app.MapGet("/health/ready", () => Results.Text($$"""{"status":"ok","revision":"{{BuildInfo.Revision}}"}""", "application/json")).AllowAnonymous();

app.MapAccountEndpoints();
app.MapMfaEndpoints();
app.MapAdminIdentityEndpoints();
app.MapTerminalEndpoints();
app.MapSftpEndpoints();
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var appLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WebSshTerminal");
lifetime.ApplicationStarted.Register(() => Log.Started(appLog, BuildInfo.Revision));
lifetime.ApplicationStopping.Register(() =>
{
    Log.Stopping(appLog);
    app.Services.GetRequiredService<LiveSessions>().CancelAll(); // tmux continua al servidor remot
});
lifetime.ApplicationStopped.Register(() => Log.Stopped(appLog));

await app.RunAsync();
return 0;

static bool IsAllowedWhileChangingPassword(PathString p) =>
    p.StartsWithSegments("/password") || p.StartsWithSegments("/account") || p.StartsWithSegments("/health")
    || p.StartsWithSegments("/_framework") || p.StartsWithSegments("/_content") || p.StartsWithSegments("/branding")
    || p.Value is "/app.css" or "/WebSshTerminal.styles.css" or "/login";

/// <summary>Punt d'entrada per a WebApplicationFactory.</summary>
public partial class Program;
