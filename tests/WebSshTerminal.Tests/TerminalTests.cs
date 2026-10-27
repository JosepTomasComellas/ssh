using System.Net;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebSshTerminal.Data;
using WebSshTerminal.Terminal;

namespace WebSshTerminal.Tests;

/// <summary>Proves reals contra el contenidor OpenSSH de fixture: PTY, tmux, host key, aïllament i orígens.</summary>
[Collection("env")]
public sealed class TerminalTests(SshFixture fx)
{
    private const string Ready = "bash*"; // finestra activa a la línia d'estat de tmux: tmux ja ha pres el control

    [Fact]
    public async Task Interactive_terminal_runs_command_as_assigned_linux_user_and_resizes()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        var session = await h.CreateSessionAsync(alice);

        using var ws = await h.ConnectAsync(alice, session);
        await ws.ReadUntilAsync(Ready);

        await ws.SendInputAsync("printf 'hello-%s\\n' world\r");
        await ws.ReadUntilAsync("hello-world");

        await ws.SendInputAsync("printf 'user-%s\\n' \"$(whoami)\"\r");
        await ws.ReadUntilAsync("user-alice");

        await ws.SendResizeAsync(120, 50);
        await Task.Delay(500);
        await ws.SendInputAsync("printf 'size-%s\\n' \"$(stty size)\"\r");
        var output = await ws.ReadUntilAsync("size-4");
        Assert.Matches(new Regex(@"size-\d+ 120"), output);
    }

    [Fact]
    public async Task Tmux_session_survives_disconnect_and_can_be_reattached()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        var session = await h.CreateSessionAsync(alice);

        using (var first = await h.ConnectAsync(alice, session))
        {
            await first.ReadUntilAsync(Ready);
            await first.SendInputAsync("export MARK=persist-xyz\r");
            await first.SendInputAsync("printf 'set-%s\\n' ok\r");
            await first.ReadUntilAsync("set-ok");
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }

        var list = await alice.Http.GetStringAsync("/api/sessions");
        Assert.Contains(session.ToString(), list, StringComparison.Ordinal);

        using var second = await h.ConnectAsync(alice, session);
        await second.ReadUntilAsync(Ready);
        await second.SendInputAsync("printf 'got-%s\\n' \"$MARK\"\r");
        await second.ReadUntilAsync("got-persist-xyz");
    }

    [Fact]
    public async Task Another_web_user_cannot_attach_to_or_delete_someone_elses_session()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        var bob = await h.AddUserAsync("bob-web", "bob", fx.BobPrivateKey);
        var aliceSession = await h.CreateSessionAsync(alice);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.ConnectAsync(bob, aliceSession));
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);

        var del = await bob.Http.SendAsync(h.Api(HttpMethod.Delete, $"/api/sessions/{aliceSession}"));
        Assert.True(del.StatusCode == HttpStatusCode.NotFound, $"{del.StatusCode} Allow={string.Join(',', del.Content.Headers.Allow)} url=/api/sessions/{aliceSession}");
        Assert.DoesNotContain(aliceSession.ToString(), await bob.Http.GetStringAsync("/api/sessions"), StringComparison.Ordinal);

        // I la sessió de l'Alice continua intacta.
        using var ws = await h.ConnectAsync(alice, aliceSession);
        await ws.ReadUntilAsync(Ready);
    }

    [Fact]
    public async Task Each_web_user_gets_only_their_own_linux_identity()
    {
        using var h = new TerminalHarness(fx);
        var bob = await h.AddUserAsync("bob-web", "bob", fx.BobPrivateKey);
        using var ws = await h.ConnectAsync(bob, await h.CreateSessionAsync(bob));
        await ws.ReadUntilAsync(Ready);
        await ws.SendInputAsync("printf 'user-%s\\n' \"$(whoami)\"\r");
        await ws.ReadUntilAsync("user-bob");
    }

    [Fact]
    public async Task Changed_host_key_is_rejected_and_audited()
    {
        var parts = fx.HostKeyLine.Split(' ');
        var tampered = parts[1].ToCharArray();
        tampered[40] = tampered[40] == 'A' ? 'B' : 'A';
        using var h = new TerminalHarness(fx, knownHosts: $"[127.0.0.1]:{fx.Port} {parts[0]} {new string(tampered)}\n");
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        var session = await h.CreateSessionAsync(alice);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.ConnectAsync(alice, session));
        Assert.Contains("502", ex.Message, StringComparison.Ordinal);

        using var scope = h.App.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Operation == AuditOps.SshConnect && a.Result == AuditResults.HostKey));
    }

    [Fact]
    public async Task Unknown_host_is_rejected()
    {
        using var h = new TerminalHarness(fx, knownHosts: "# buit\n");
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await h.ConnectAsync(alice, await h.CreateSessionAsync(alice)));
        Assert.Contains("502", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://ssh.example.test")]
    [InlineData(null)]
    public async Task Websocket_with_foreign_or_missing_origin_is_rejected(string? origin)
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        var session = await h.CreateSessionAsync(alice);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.ConnectAsync(alice, session, origin));
        Assert.Contains("403", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_websocket_is_not_upgraded()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        var session = await h.CreateSessionAsync(alice);
        var anonymous = alice with { Cookie = "" };
        await Assert.ThrowsAnyAsync<Exception>(() => h.ConnectAsync(anonymous, session));
    }

    [Fact]
    public async Task Api_mutations_require_same_origin_and_custom_header()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);

        var noHeader = new HttpRequestMessage(HttpMethod.Post, "/api/sessions");
        noHeader.Headers.Add("Origin", h.Env.Origin);
        var noHeaderRes = await alice.Http.SendAsync(noHeader);
        Assert.True(noHeaderRes.StatusCode == HttpStatusCode.Forbidden, $"{noHeaderRes.StatusCode}: {(await noHeaderRes.Content.ReadAsStringAsync())[..Math.Min(400, (await noHeaderRes.Content.ReadAsStringAsync()).Length)]}");

        var foreign = h.Api(HttpMethod.Post, "/api/sessions");
        foreign.Headers.Remove("Origin");
        foreign.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.Http.SendAsync(foreign)).StatusCode);
    }

    [Fact]
    public async Task User_without_identity_cannot_create_sessions_and_limit_is_enforced()
    {
        using var h = new TerminalHarness(fx, e => e.MaxSessionsPerUser = 1);
        var nobody = await h.AddUserAsync("nobody-web");
        Assert.Equal(HttpStatusCode.Conflict, (await nobody.Http.SendAsync(h.Api(HttpMethod.Post, "/api/sessions"))).StatusCode);

        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        await h.CreateSessionAsync(alice);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await alice.Http.SendAsync(h.Api(HttpMethod.Post, "/api/sessions"))).StatusCode);
    }

    [Fact]
    public async Task Identity_cannot_be_shared_between_two_web_users()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        var other = await h.AddUserAsync("other-web");

        using var scope = h.App.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var identityId = (await db.Users.FirstAsync(u => u.Id == alice.Id)).SshIdentityId;
        var result = await scope.ServiceProvider.GetRequiredService<SshIdentityService>().AssignAsync(other.Id, identityId, "test");
        Assert.Equal(IdentityResult2.AlreadyAssigned, result);
    }
}
