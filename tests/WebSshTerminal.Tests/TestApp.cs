using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace WebSshTerminal.Tests;

/// <summary>Program llegeix la configuració en crear el builder: per això va per variables d'entorn,
/// i les proves que n'usen no poden córrer en paral·lel entre elles.</summary>
[CollectionDefinition("env", DisableParallelization = true)]
public sealed class EnvDefinition;

public sealed partial class TestApp : IDisposable
{
    public const string BootstrapPassword = "Bootstrap-Pass-1234";

    private readonly TestEnv _env;
    private readonly bool _ownsEnv;
    public WebApplicationFactory<Program> Factory { get; }

    public TestApp(TestEnv? shared = null, bool zeroStampInterval = true)
    {
        _ownsEnv = shared is null;
        _env = (shared ?? new TestEnv()).Apply();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureServices(s =>
            {
                if (zeroStampInterval) s.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
            });
        });
    }

    public HttpClient NewClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public static async Task<string> TokenAsync(HttpClient c, string page)
    {
        var html = await c.GetStringAsync(page);
        var m = TokenRegex().Match(html);
        Assert.True(m.Success, $"No hi ha token antiforgery a {page}");
        return WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient c, string url, string tokenPage, Dictionary<string, string> form, bool withToken = true)
    {
        if (withToken) form["__RequestVerificationToken"] = await TokenAsync(c, tokenPage);
        return await c.PostAsync(url, new FormUrlEncodedContent(form));
    }

    public static Task<HttpResponseMessage> LoginAsync(HttpClient c, string user, string pass) =>
        PostFormAsync(c, "/account/login", "/login", new() { ["username"] = user, ["password"] = pass });

    /// <summary>Entra com a admin i fa el canvi obligatori de contrasenya.</summary>
    public static async Task<string> LoginAdminAsync(HttpClient c, string newPassword = "Admin-New-Password-99")
    {
        var r = await LoginAsync(c, "admin", BootstrapPassword);
        Assert.Equal("/password", r.Headers.Location?.OriginalString);
        var p = await PostFormAsync(c, "/account/password", "/password",
            new() { ["current"] = BootstrapPassword, ["next"] = newPassword, ["confirm"] = newPassword });
        Assert.Equal("/", p.Headers.Location?.OriginalString);
        return newPassword;
    }

    public void Dispose()
    {
        Factory.Dispose();
        if (_ownsEnv) _env.Dispose();
    }

    [GeneratedRegex("__RequestVerificationToken[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();
}
