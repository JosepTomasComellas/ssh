using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using WebSshTerminal.Auth;
using WebSshTerminal.Data;

namespace WebSshTerminal.Tests;

[Collection("env")]
public sealed class AdminCommandsTests
{
    private const string Email = "admin@escola.cat";

    private static async Task<HttpClient> LoginChangeAsync(TestApp app, string password)
    {
        var c = app.NewClient();
        var login = await TestApp.LoginAsync(c, Email, password);
        Assert.Equal("/password", login.Headers.Location?.OriginalString);
        const string next = "Nova-Contrasenya-Segura-77";
        var change = await TestApp.PostFormAsync(c, "/account/password", "/password", new() { ["current"] = password, ["next"] = next, ["confirm"] = next });
        Assert.NotNull(change.Headers.Location);
        return c;
    }

    [Fact]
    public async Task Created_admin_can_administer_users_without_otp_while_the_grace_period_lasts()
    {
        using var env = new TestEnv { RequireMfa = true };
        using var app = new TestApp(env);
        var result = await AdminCommands.CreateAdminAsync(app.Factory.Services, Email, graceDays: 7);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(24, result.Password!.Length);
        Assert.True(result.MfaGraceUntilUtc > DateTime.UtcNow.AddDays(6));

        using var c = await LoginChangeAsync(app, result.Password);
        // Amb RequireMfa=true i sense TOTP, l'exempció temporal deixa treballar:
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/admin/users")).StatusCode);
        var created = await TestApp.PostFormAsync(c, "/admin/users", "/admin/users", new() { ["username"] = "profe1@escola.cat", ["role"] = "user" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        // El nou admin ho és de debò, i la contrasenya no apareix a cap log.
        using var scope = app.Factory.Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Assert.True(await um.IsInRoleAsync((await um.FindByNameAsync(Email))!, Roles.Admin));
        Assert.DoesNotContain(result.Password, app.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mfa_becomes_mandatory_again_when_the_grace_period_expires()
    {
        using var env = new TestEnv { RequireMfa = true };
        using var app = new TestApp(env);
        var result = await AdminCommands.CreateAdminAsync(app.Factory.Services, Email, graceDays: 7);

        using (var scope = app.Factory.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var u = (await um.FindByNameAsync(Email))!;
            u.MfaGraceUntilUtc = DateTime.UtcNow.AddMinutes(-1); // caducada
            await um.UpdateAsync(u);
        }

        using var c = await LoginChangeAsync(app, result.Password!);
        Assert.Equal("/account/mfa", (await c.GetAsync("/")).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Zero_grace_days_means_mfa_is_required_from_the_first_login()
    {
        using var env = new TestEnv { RequireMfa = true };
        using var app = new TestApp(env);
        var result = await AdminCommands.CreateAdminAsync(app.Factory.Services, Email, graceDays: 0);
        Assert.Null(result.MfaGraceUntilUtc);
        using var c = await LoginChangeAsync(app, result.Password!);
        Assert.Equal("/account/mfa", (await c.GetAsync("/")).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Existing_users_are_never_overwritten_and_bad_input_is_rejected()
    {
        using var app = new TestApp();
        Assert.False((await AdminCommands.CreateAdminAsync(app.Factory.Services, "admin", 7)).Ok); // el bootstrap ja hi és (i no és un correu)
        var first = await AdminCommands.CreateAdminAsync(app.Factory.Services, Email, 7);
        Assert.True(first.Ok);
        var again = await AdminCommands.CreateAdminAsync(app.Factory.Services, Email, 7);
        Assert.False(again.Ok);
        Assert.Null(again.Password);
        Assert.False((await AdminCommands.CreateAdminAsync(app.Factory.Services, "no-es-un-correu", 7)).Ok);
        Assert.False((await AdminCommands.CreateAdminAsync(app.Factory.Services, "x@escola.cat", 31)).Ok);
        Assert.False((await AdminCommands.CreateAdminAsync(app.Factory.Services, "x@escola.cat", -1)).Ok);
    }

    [Fact]
    public async Task Cli_prints_the_password_to_stdout_only_and_returns_exit_codes()
    {
        using var app = new TestApp();
        var stdout = new StringWriter();
        var old = Console.Out;
        Console.SetOut(stdout);
        try
        {
            Assert.Equal(0, await AdminCommands.RunAsync(app.Factory.Services, ["--create-admin", "cli@escola.cat", "--mfa-grace-days", "3"]));
            Assert.Equal(1, await AdminCommands.RunAsync(app.Factory.Services, ["--create-admin", "cli@escola.cat"])); // ja existeix
            Assert.Equal(2, await AdminCommands.RunAsync(app.Factory.Services, ["--create-admin"]));
            Assert.Equal(2, await AdminCommands.RunAsync(app.Factory.Services, ["--create-admin", "a@b.cat", "--mfa-grace-days", "x"]));
        }
        finally { Console.SetOut(old); }
        Assert.Contains("Contrasenya inicial", stdout.ToString(), StringComparison.Ordinal);
    }
}
