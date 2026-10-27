using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebSshTerminal.Data;
using WebSshTerminal.Terminal;

namespace WebSshTerminal.Tests;

[Collection("env")]
public sealed class SftpTests(SshFixture fx)
{
    private const string Canary = "CANARY-file-content-7f3a91";

    private static Task<HttpResponseMessage> Upload(TerminalHarness h, WebUser u, string path, string content, bool overwrite = false)
    {
        var req = h.Api(HttpMethod.Post, $"/api/files/upload?path={Uri.EscapeDataString(path)}&overwrite={overwrite.ToString().ToLowerInvariant()}");
        req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return u.Http.SendAsync(req);
    }

    private static Task<HttpResponseMessage> Download(TerminalHarness h, WebUser u, string path) =>
        u.Http.GetAsync($"/api/files/download?path={Uri.EscapeDataString(path)}");

    [Fact]
    public async Task Upload_and_download_round_trip_through_sftp()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        await fx.ExecAsync("alice", "mkdir -p ~/up");

        var up = await Upload(h, alice, "up/a.txt", Canary);
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        Assert.Equal(Canary, (await fx.ExecAsync("alice", "cat ~/up/a.txt")).Trim());

        var down = await Download(h, alice, "up/a.txt");
        Assert.Equal(HttpStatusCode.OK, down.StatusCode);
        Assert.Equal(Canary, await down.Content.ReadAsStringAsync());
        Assert.Equal("attachment", down.Content.Headers.ContentDisposition?.DispositionType);

        var list = await alice.Http.GetStringAsync("/api/files/list?path=up");
        Assert.Contains("a.txt", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overwrite_requires_explicit_confirmation()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        await fx.ExecAsync("alice", "mkdir -p ~/ow");

        Assert.Equal(HttpStatusCode.Created, (await Upload(h, alice, "ow/f.txt", "one")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(h, alice, "ow/f.txt", "two")).StatusCode);
        Assert.Equal("one", (await fx.ExecAsync("alice", "cat ~/ow/f.txt")).Trim());
        Assert.Equal(HttpStatusCode.Created, (await Upload(h, alice, "ow/f.txt", "two", overwrite: true)).StatusCode);
        Assert.Equal("two", (await fx.ExecAsync("alice", "cat ~/ow/f.txt")).Trim());
        Assert.DoesNotContain(".upload-", await fx.ExecAsync("alice", "ls -a ~/ow"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../bob/x.txt")]
    [InlineData("a/../../x.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("a\\b")]
    [InlineData("a\u0000b")]
    [InlineData("..")]
    public async Task Invalid_or_escaping_paths_are_rejected(string path)
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(h, alice, path, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Download(h, alice, path)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Http.GetAsync($"/api/files/list?path={Uri.EscapeDataString(path)}")).StatusCode);
    }

    [Fact]
    public async Task Symlinks_are_not_followed()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        await fx.ExecAsync("alice", "ln -sfn /etc ~/linkdir");
        await fx.ExecAsync("alice", "ln -sfn /etc/hostname ~/linkfile");

        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Http.GetAsync("/api/files/list?path=linkdir")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Download(h, alice, "linkdir/passwd")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Download(h, alice, "linkfile")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(h, alice, "linkdir/evil.txt", "x")).StatusCode);
    }

    [Fact]
    public async Task Transfers_above_the_limit_are_refused_and_leave_no_partial_file()
    {
        // MaxTransferBytes = 1024 (mínim permès per la validació)
        using var small = new TerminalHarness(fx, e => e.MaxTransferBytes = 1024);
        var alice = await small.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        await fx.ExecAsync("alice", "mkdir -p ~/big");

        var res = await Upload(small, alice, "big/f.bin", new string('x', 4096));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        Assert.Equal("", (await fx.ExecAsync("alice", "ls -A ~/big")).Trim());
    }

    [Fact]
    public async Task Each_user_only_reaches_their_own_home()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        var bob = await h.AddUserAsync("bob-web", "bob", fx.BobPrivateKey);

        Assert.Equal(HttpStatusCode.Created, (await Upload(h, bob, "only-bob.txt", "b")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Download(h, alice, "only-bob.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Download(h, alice, "../bob/only-bob.txt")).StatusCode);
        Assert.Equal("", (await fx.ExecAsync("alice", "ls ~/only-bob.txt 2>/dev/null || true")).Trim());
    }

    [Fact]
    public async Task User_without_identity_cannot_use_sftp_and_mutations_need_the_guard()
    {
        using var h = new TerminalHarness(fx);
        var nobody = await h.AddUserAsync("nobody-web");
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(h, nobody, "x.txt", "x")).StatusCode);

        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        var noGuard = new HttpRequestMessage(HttpMethod.Post, "/api/files/upload?path=x.txt") { Content = new ByteArrayContent([1]) };
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.Http.SendAsync(noGuard)).StatusCode);
    }

    [Fact]
    public async Task Secrets_and_content_never_reach_database_or_logs()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        await fx.ExecAsync("alice", "mkdir -p ~/leak");

        Assert.Equal(HttpStatusCode.Created, (await Upload(h, alice, "leak/CANARY-filename-91b2.txt", Canary)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Download(h, alice, "leak/CANARY-filename-91b2.txt")).StatusCode);

        var session = await h.CreateSessionAsync(alice);
        using (var ws = await h.ConnectAsync(alice, session))
        {
            await ws.ReadUntilAsync("bash*");
            await ws.SendInputAsync("printf 'out-%s\\n' CANARY-terminal-text-55c0\r");
            await ws.ReadUntilAsync("out-CANARY-terminal-text-55c0");
        }
        await Task.Delay(500);

        var keyBody = fx.AlicePrivateKey.Split('\n').Where(l => l.Length > 20 && !l.StartsWith("-----", StringComparison.Ordinal)).First();
        string[] canaries =
        [
            Canary, "CANARY-filename-91b2", "CANARY-terminal-text-55c0", keyBody, "OPENSSH PRIVATE KEY",
            TerminalHarness.UserPassword, TestApp.BootstrapPassword, new string('a', 64),
        ];

        // Base de dades (inclou WAL) i claus de Data Protection: només el blob xifrat, mai el text clar.
        foreach (var file in Directory.GetFiles(h.Env.Dir, "webssh.db*"))
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            await fs.CopyToAsync(ms);
            var text = Encoding.Latin1.GetString(ms.ToArray());
            foreach (var c in canaries.Where(c => c != TerminalHarness.UserPassword)) // el hash de la contrasenya no la conté
                Assert.DoesNotContain(c, text, StringComparison.Ordinal);
            Assert.DoesNotContain(TerminalHarness.UserPassword, text, StringComparison.Ordinal);
        }

        // Logs (missatge, estat estructurat i excepcions).
        var logs = h.App.Logs.All;
        Assert.NotEmpty(h.App.Logs.Lines);
        foreach (var c in canaries) Assert.DoesNotContain(c, logs, StringComparison.Ordinal);

        // Auditoria: hi ha esdeveniments de transferència, amb bytes agregats i cap contingut.
        using var scope = h.App.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var events = await db.AuditEvents.AsNoTracking().ToListAsync();
        Assert.Contains(events, e => e.Operation == AuditOps.SftpUpload && e.Amount == Canary.Length);
        Assert.Contains(events, e => e.Operation == AuditOps.SessionAttach);
    }
}
