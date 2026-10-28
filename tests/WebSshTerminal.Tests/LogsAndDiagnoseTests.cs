using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebSshTerminal.Infrastructure;

namespace WebSshTerminal.Tests;

public class LogBufferTests
{
    private static LogEntry E(LogLevel l, string msg, string cat = "App.Cat", int ev = 0, string? ex = null, DateTimeOffset? t = null) =>
        new(t ?? DateTimeOffset.Now, l, cat, ev, msg, ex);

    [Fact]
    public void Query_filters_by_level_text_category_event_and_window_newest_first()
    {
        var b = new LogBuffer();
        b.Add(E(LogLevel.Information, "arrencada"));
        b.Add(E(LogLevel.Warning, "Connexió SSH fallida", "App.Ssh.SshConnector", 1400));
        b.Add(E(LogLevel.Error, "boom", ex: "System.TimeoutException: x"));
        b.Add(E(LogLevel.Warning, "antic", t: DateTimeOffset.Now.AddHours(-5)));

        // L'ordre és la d'inserció invertida: la darrera entrada afegida surt primera.
        Assert.Equal(["antic", "boom", "Connexió SSH fallida"], b.Query(new LogFilter(LogLevel.Warning)).Select(e => e.Message));
        Assert.Single(b.Query(new LogFilter(LogLevel.Error)));
        Assert.Single(b.Query(new LogFilter(LogLevel.Trace, EventId: 1400)));
        Assert.Single(b.Query(new LogFilter(LogLevel.Trace, Category: "sshconnector")));
        Assert.Single(b.Query(new LogFilter(LogLevel.Trace, Text: "timeoutexception"))); // també cerca a l'excepció
        Assert.DoesNotContain(b.Query(new LogFilter(LogLevel.Trace, Window: TimeSpan.FromHours(1))), e => e.Message == "antic");
    }

    [Fact]
    public void Buffer_is_bounded_and_keeps_the_newest_entries()
    {
        var b = new LogBuffer();
        for (var i = 0; i < LogBuffer.Capacity + 50; i++) b.Add(E(LogLevel.Warning, "m" + i));
        Assert.True(b.Count <= LogBuffer.Capacity);
        Assert.Equal("m" + (LogBuffer.Capacity + 49), b.Query(new LogFilter(LogLevel.Warning, Limit: 1))[0].Message);
    }

    [Fact]
    public void Huge_exceptions_are_truncated()
    {
        var b = new LogBuffer();
        b.Add(E(LogLevel.Error, "x", ex: new string('e', 10_000)));
        Assert.True(b.Query(new LogFilter(LogLevel.Error))[0].Exception!.Length < 4100);
    }
}

[Collection("env")]
public sealed class LogsAndDiagnoseTests(SshFixture fx)
{
    private static HttpRequestMessage Diagnose(TerminalHarness h, Guid id) => h.Api(HttpMethod.Post, $"/api/sessions/{id}/diagnose");

    [Fact]
    public async Task Admin_sees_failed_ssh_connections_in_the_logs_page_and_regular_users_cannot()
    {
        var parts = fx.HostKeyLine.Split(' ');
        var bad = parts[1].ToCharArray();
        bad[40] = bad[40] == 'A' ? 'B' : 'A';
        using var h = new TerminalHarness(fx, knownHosts: $"[127.0.0.1]:{fx.Port} {parts[0]} {new string(bad)}\n");
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        var session = await h.CreateSessionAsync(alice);
        await Assert.ThrowsAnyAsync<Exception>(() => h.ConnectAsync(alice, session)); // 502

        using var admin = h.App.NewClient();
        await TestApp.LoginAdminAsync(admin);
        var html = WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/logs?level=Warning&event=1400&minutes=0"));
        Assert.Contains("categoria HostKeyRejected", html, StringComparison.Ordinal);
        Assert.Contains($"destí 127.0.0.1:{fx.Port}", html, StringComparison.Ordinal);

        // Un filtre que no hi casa no mostra l'entrada; el de text sí.
        Assert.DoesNotContain("categoria HostKeyRejected", await admin.GetStringAsync("/admin/logs?level=Error&minutes=0"), StringComparison.Ordinal);
        Assert.Contains("categoria HostKeyRejected", await admin.GetStringAsync("/admin/logs?level=Warning&q=hostkeyrejected&minutes=0"), StringComparison.Ordinal);

        // Els usuaris normals no hi accedeixen.
        var denied = await alice.Http.GetAsync("/admin/logs");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("/denied", denied.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logs_page_never_shows_secrets_or_session_content()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        using var ws = await h.ConnectAsync(alice, await h.CreateSessionAsync(alice));
        await ws.ReadUntilAsync("bash*");
        await ws.SendInputAsync("printf 'out-%s\\n' CANARY-LOGS-PAGE\r");
        await ws.ReadUntilAsync("out-CANARY-LOGS-PAGE");

        using var admin = h.App.NewClient();
        await TestApp.LoginAdminAsync(admin);
        var html = await admin.GetStringAsync("/admin/logs?level=Trace&minutes=0&limit=1000");
        Assert.DoesNotContain("CANARY-LOGS-PAGE", html, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENSSH PRIVATE KEY", html, StringComparison.Ordinal);
        Assert.DoesNotContain(TerminalHarness.UserPassword, html, StringComparison.Ordinal);
        Assert.DoesNotContain(TestApp.BootstrapPassword, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnose_reports_a_safe_category_and_is_owner_scoped_and_rate_limited()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        var bob = await h.AddUserAsync("bob-web", "bob", fx.BobPrivateKey);
        var session = await h.CreateSessionAsync(alice);

        var ok = await alice.Http.SendAsync(Diagnose(h, session));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("ok", (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());

        // Immediatament després: limitació de freqüència.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await alice.Http.SendAsync(Diagnose(h, session))).StatusCode);

        // Una sessió aliena és inexistent (i no consumeix res de la víctima).
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Http.SendAsync(Diagnose(h, session))).StatusCode);

        // Sense la capçalera/origen de l'API, el guard la rebutja.
        var unguarded = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{session}/diagnose");
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.Http.SendAsync(unguarded)).StatusCode);
    }

    [Fact]
    public async Task Diagnose_distinguishes_hostkey_auth_and_unreachable_failures()
    {
        // Clau de host canviada
        var parts = fx.HostKeyLine.Split(' ');
        var bad = parts[1].ToCharArray();
        bad[40] = bad[40] == 'A' ? 'B' : 'A';
        using (var h = new TerminalHarness(fx, knownHosts: $"[127.0.0.1]:{fx.Port} {parts[0]} {new string(bad)}\n"))
        {
            var u = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
            var res = await u.Http.SendAsync(Diagnose(h, await h.CreateSessionAsync(u)));
            Assert.Equal("hostkey", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());
        }
        // Clau no autoritzada: la d'Alice usada amb el compte «bob»
        using (var h = new TerminalHarness(fx))
        {
            var u = await h.AddUserAsync("mix-web", "bob", fx.AlicePrivateKey);
            var res = await u.Http.SendAsync(Diagnose(h, await h.CreateSessionAsync(u)));
            Assert.Equal("authfailed", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());
        }
        // Port on no escolta ningú
        using (var h = new TerminalHarness(fx, e => e.SshPort = 1, knownHosts: "[127.0.0.1]:1 " + fx.HostKeyLine + "\n"))
        {
            var u = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
            var res = await u.Http.SendAsync(Diagnose(h, await h.CreateSessionAsync(u)));
            Assert.Equal("unreachable", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());
        }
    }
}
