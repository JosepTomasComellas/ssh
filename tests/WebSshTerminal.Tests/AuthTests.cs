using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using WebSshTerminal.Data;
using Microsoft.AspNetCore.Identity;

namespace WebSshTerminal.Tests;

[Collection("env")]
public sealed partial class AuthTests
{
    [Fact]
    public async Task Anonymous_requests_are_redirected_to_login()
    {
        using var app = new TestApp();
        using var c = app.NewClient();
        var r = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/login", r.Headers.Location!.AbsolutePath);
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Login_without_antiforgery_token_is_rejected()
    {
        using var app = new TestApp();
        using var c = app.NewClient();
        var r = await TestApp.PostFormAsync(c, "/account/login", "/login",
            new() { ["username"] = "admin", ["password"] = TestApp.BootstrapPassword }, withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_admin_must_change_password_before_anything_else()
    {
        using var app = new TestApp();
        using var c = app.NewClient();

        var login = await TestApp.LoginAsync(c, "admin", TestApp.BootstrapPassword);
        Assert.Equal("/password", login.Headers.Location?.OriginalString);

        var home = await c.GetAsync("/");
        Assert.Equal("/password", home.Headers.Location?.OriginalString);

        var weak = await TestApp.PostFormAsync(c, "/account/password", "/password",
            new() { ["current"] = TestApp.BootstrapPassword, ["next"] = "short", ["confirm"] = "short" });
        Assert.Contains("error=policy", weak.Headers.Location!.OriginalString, StringComparison.Ordinal);

        await TestApp.PostFormAsync(c, "/account/password", "/password",
            new() { ["current"] = TestApp.BootstrapPassword, ["next"] = "Admin-New-Password-99", ["confirm"] = "Admin-New-Password-99" });
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Auth_cookie_is_httponly_and_samesite()
    {
        using var app = new TestApp();
        using var c = app.NewClient();
        var r = await TestApp.LoginAsync(c, "admin", TestApp.BootstrapPassword);
        var cookie = Assert.Single(r.Headers.GetValues("Set-Cookie"), s => s.StartsWith("webssh.auth=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Account_locks_after_repeated_failures_even_with_correct_password()
    {
        using var app = new TestApp();
        using var c = app.NewClient();
        for (var i = 0; i < 5; i++)
            Assert.Contains("error=1", (await TestApp.LoginAsync(c, "admin", "wrong-password-" + i)).Headers.Location!.OriginalString, StringComparison.Ordinal);

        var r = await TestApp.LoginAsync(c, "admin", TestApp.BootstrapPassword);
        Assert.Contains("error=1", r.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_rate_limit_returns_429()
    {
        using var app = new TestApp();
        using var c = app.NewClient();
        var token = await TestApp.TokenAsync(c, "/login");
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            var r = await c.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["username"] = "nobody" + i, ["password"] = "x", ["__RequestVerificationToken"] = token }));
            codes.Add(r.StatusCode);
        }
        Assert.True(codes[^1] == HttpStatusCode.TooManyRequests, string.Join(",", codes));
    }

    [Fact]
    public async Task Unknown_user_and_wrong_password_are_indistinguishable()
    {
        using var app = new TestApp();
        using var c = app.NewClient();
        var a = await TestApp.LoginAsync(c, "ghost", "whatever-1234567");
        var b = await TestApp.LoginAsync(c, "admin", "whatever-1234567");
        Assert.Equal(a.Headers.Location, b.Headers.Location);
    }

    [Fact]
    public async Task Regular_user_cannot_use_admin_area_and_admin_can_create_users()
    {
        using var app = new TestApp();
        using var admin = app.NewClient();
        await TestApp.LoginAdminAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin/users")).StatusCode);

        var created = await TestApp.PostFormAsync(admin, "/admin/users", "/admin/users", new() { ["username"] = "alice", ["role"] = "user" });
        var body = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Contains("no-store", created.Headers.CacheControl?.ToString() ?? "", StringComparison.Ordinal);
        var temp = TempRegex().Match(body).Groups[1].Value;
        Assert.NotEmpty(temp);

        using var alice = app.NewClient();
        var login = await TestApp.LoginAsync(alice, "alice", temp);
        Assert.Equal("/password", login.Headers.Location?.OriginalString);
        await TestApp.PostFormAsync(alice, "/account/password", "/password",
            new() { ["current"] = temp, ["next"] = "Alice-Password-1234", ["confirm"] = "Alice-Password-1234" });

        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/")).StatusCode);
        var denied = await alice.GetAsync("/admin/users");
        Assert.Equal("/denied", new Uri(denied.Headers.Location!.OriginalString, UriKind.RelativeOrAbsolute).IsAbsoluteUri
            ? denied.Headers.Location!.AbsolutePath : denied.Headers.Location!.OriginalString.Split('?')[0]);

        // Els endpoints d'administració també estan protegits per al rol, no només la pàgina.
        var post = await TestApp.PostFormAsync(alice, "/admin/users", "/password", new() { ["username"] = "mallory", ["role"] = "admin" });
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
    }

    [Fact]
    public async Task Revoking_sessions_invalidates_existing_cookie()
    {
        using var app = new TestApp();
        using var admin = app.NewClient();
        await TestApp.LoginAdminAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/")).StatusCode);

        using (var scope = app.Factory.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await um.UpdateSecurityStampAsync((await um.FindByNameAsync("admin"))!);
        }
        var after = await admin.GetAsync("/");
        Assert.True(after.StatusCode == HttpStatusCode.Redirect, (await after.Content.ReadAsStringAsync())[..Math.Min(1500, (await after.Content.ReadAsStringAsync()).Length)]);
    }

    [Fact]
    public async Task Disabled_user_cannot_log_in()
    {
        using var app = new TestApp();
        using var admin = app.NewClient();
        await TestApp.LoginAdminAsync(admin);
        var created = await TestApp.PostFormAsync(admin, "/admin/users", "/admin/users", new() { ["username"] = "bob", ["role"] = "user" });
        var temp = TempRegex().Match(await created.Content.ReadAsStringAsync()).Groups[1].Value;

        string id;
        using (var scope = app.Factory.Services.CreateScope())
            id = (await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByNameAsync("bob"))!.Id;
        await TestApp.PostFormAsync(admin, $"/admin/users/{id}/disable", "/admin/users", new() { ["disable"] = "true" });

        using var bob = app.NewClient();
        Assert.Contains("error=1", (await TestApp.LoginAsync(bob, "bob", temp)).Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_does_not_recreate_or_reset_admin()
    {
        using var env = new TestEnv();
        string newPassword;
        using (var first = new TestApp(env))
        using (var c = first.NewClient())
            newPassword = await TestApp.LoginAdminAsync(c);

        using var second = new TestApp(env);
        using var c2 = second.NewClient();
        Assert.Contains("error=1", (await TestApp.LoginAsync(c2, "admin", TestApp.BootstrapPassword)).Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal("/", (await TestApp.LoginAsync(c2, "admin", newPassword)).Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/ok", true)]
    [InlineData("/", true)]
    [InlineData("//evil.test", false)]
    [InlineData("/\\evil.test", false)]
    [InlineData("https://evil.test", false)]
    [InlineData("", false)]
    public void Return_url_must_be_local(string url, bool expected) =>
        Assert.Equal(expected, WebSshTerminal.Auth.AccountEndpoints.IsSafeLocal(url));

    [GeneratedRegex("id=\"temp\">([^<]+)<")]
    private static partial Regex TempRegex();
}
