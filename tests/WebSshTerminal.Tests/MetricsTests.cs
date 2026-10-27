using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using WebSshTerminal.Infrastructure;

namespace WebSshTerminal.Tests;

[Collection("env")]
public sealed partial class MetricsTests(SshFixture fx)
{
    private const string Token = "0123456789abcdef0123456789abcdef-token";

    private static async Task<(int Status, string Body)> Scrape(string path = "/metrics", string? bearer = null, string? configuredToken = Token, string method = "GET")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (bearer is not null) ctx.Request.Headers.Authorization = "Bearer " + bearer;
        ctx.Response.Body = new MemoryStream();
        await MetricsEndpoint.HandleAsync(ctx, configuredToken);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync());
    }

    [Fact]
    public async Task Scrape_requires_the_bearer_token_when_configured()
    {
        Assert.Equal(401, (await Scrape()).Status);
        Assert.Equal(401, (await Scrape(bearer: "wrong-token-wrong-token-wrong-token!!")).Status);
        Assert.Equal(401, (await Scrape(bearer: Token + "x")).Status);
        var ok = await Scrape(bearer: Token);
        Assert.Equal(200, ok.Status);
        Assert.Contains("# TYPE", ok.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Management_listener_serves_only_metrics()
    {
        Assert.Equal(404, (await Scrape("/", bearer: Token)).Status);
        Assert.Equal(404, (await Scrape("/admin/users", bearer: Token)).Status);
        Assert.Equal(404, (await Scrape("/metrics", bearer: Token, method: "POST")).Status);
    }

    [Fact]
    public void Management_requests_are_identified_by_socket_port_not_headers()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("evil.test", 9091); // capçalera Host falsificada
        ctx.Connection.LocalPort = 8080;
        Assert.False(MetricsEndpoint.IsManagementRequest(ctx, 9091));
        ctx.Connection.LocalPort = 9091;
        Assert.True(MetricsEndpoint.IsManagementRequest(ctx, 9091));
        Assert.False(MetricsEndpoint.IsManagementRequest(ctx, 0)); // desactivat
    }

    [Fact]
    public async Task Metrics_are_not_reachable_through_the_user_facing_port()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.Http.GetAsync("/metrics")).StatusCode);

        using var anon = h.App.NewClient();
        Assert.NotEqual(HttpStatusCode.OK, (await anon.GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task Scrape_has_product_metrics_but_no_pii_secrets_or_high_cardinality_labels()
    {
        using var h = new TerminalHarness(fx);
        var alice = await h.AddUserAsync("alice-web", "alice", fx.AlicePrivateKey);
        await fx.ExecAsync("alice", "mkdir -p ~/m");

        var session = await h.CreateSessionAsync(alice);
        using (var ws = await h.ConnectAsync(alice, session))
        {
            await ws.ReadUntilAsync("bash*");
            await ws.SendInputAsync("printf 'out-%s\\n' CANARY-metrics-terminal\r");
            await ws.ReadUntilAsync("out-CANARY-metrics-terminal");
        }
        var up = h.Api(HttpMethod.Post, "/api/files/upload?path=m%2FCANARY-metrics-file.txt");
        up.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("CANARY-metrics-content"));
        Assert.Equal(HttpStatusCode.Created, (await alice.Http.SendAsync(up)).StatusCode);
        await alice.Http.GetAsync("/");
        // Camins aleatoris i amb identificadors: cap ha d'acabar com a valor d'etiqueta.
        await alice.Http.GetAsync("/random-path-4f9c1d2e");
        await alice.Http.GetAsync($"/api/sessions/{Guid.NewGuid()}");
        await alice.Http.SendAsync(h.Api(HttpMethod.Delete, $"/api/sessions/{session}"));

        var (status, body) = await Scrape(bearer: Token);
        Assert.DoesNotContain("random-path-4f9c1d2e", body, StringComparison.Ordinal);
        Assert.Matches(@"endpoint=""/api/sessions/\{id:guid\}""", body);
        Assert.Equal(200, status);

        foreach (var metric in new[]
        {
            "webssh_terminal_sessions_active", "webssh_ssh_connections_total", "webssh_terminal_attaches_total",
            "webssh_terminal_attach_duration_seconds", "webssh_sftp_transfers_total", "webssh_sftp_bytes_total", "webssh_logins_total",
        })
            Assert.Contains(metric, body, StringComparison.Ordinal);

        string[] forbidden =
        [
            "alice", "alice-web", "CANARY", "127.0.0.1", "localhost", "ssh.example.test", session.ToString(), session.ToString("N"),
            "ssh-ed25519", fx.HostKeyLine.Split(' ')[1], "OPENSSH", TerminalHarness.UserPassword, TestApp.BootstrapPassword,
        ];
        foreach (var f in forbidden) Assert.DoesNotContain(f, body, StringComparison.OrdinalIgnoreCase);

        // Només s'admeten aquests noms d'etiqueta (conjunt tancat i de baixa cardinalitat).
        // Mètriques pròpies i HTTP: conjunt estricte. Les del runtime .NET tenen etiquetes de conjunt tancat
        // (generació de GC, mode de CPU…) i la resta de comprovacions (cadenes prohibides) també les cobreix.
        var strict = new Dictionary<string, HashSet<string>>
        {
            ["webssh_"] = ["result", "kind", "reason", "category", "direction", "le"],
            ["http_"] = ["code", "method", "controller", "action", "endpoint", "le"],
        };
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match line in LabelRegex().Matches(body))
        {
            var rule = strict.FirstOrDefault(kv => line.Value.StartsWith(kv.Key, StringComparison.Ordinal));
            if (rule.Value is null) continue;
            foreach (Match label in LabelNameRegex().Matches(line.Groups[1].Value))
                if (!rule.Value.Contains(label.Groups[1].Value))
                    unknown.Add(label.Groups[1].Value + " <- " + line.Value[..Math.Min(90, line.Value.Length)]);
        }
        Assert.True(unknown.Count == 0, "Etiquetes no previstes: " + string.Join(" | ", unknown.Take(8)));
    }

    [GeneratedRegex(@"^[a-zA-Z_:][a-zA-Z0-9_:]*\{([^}]*)\}", RegexOptions.Multiline)]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"([a-zA-Z_][a-zA-Z0-9_]*)=""")]
    private static partial Regex LabelNameRegex();
}
