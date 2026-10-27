using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using WebSshTerminal.Data;
using WebSshTerminal.Terminal;

namespace WebSshTerminal.Tests;

public sealed record WebUser(string Id, string Name, string Cookie, HttpClient Http);

/// <summary>Instància real de l'app apuntant al contenidor SSH de fixture.</summary>
public sealed class TerminalHarness : IDisposable
{
    public const string UserPassword = "User-Password-1234";
    public TestEnv Env { get; }
    public TestApp App { get; }
    public SshFixture Fx { get; }

    public TerminalHarness(SshFixture fx, Action<TestEnv>? tweak = null, string? knownHosts = null)
    {
        Fx = fx;
        Env = new TestEnv { SshHost = "127.0.0.1", SshPort = fx.Port, KnownHosts = knownHosts ?? fx.KnownHosts() };
        tweak?.Invoke(Env);
        App = new TestApp(Env);
    }

    public async Task<WebUser> AddUserAsync(string name, string? linuxUser = null, string? privateKey = null)
    {
        string id;
        using (var scope = App.Factory.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { UserName = name, LockoutEnabled = true };
            Assert.True((await um.CreateAsync(user, UserPassword)).Succeeded);
            await um.AddToRoleAsync(user, Roles.User);
            id = user.Id;

            if (linuxUser is not null)
            {
                var ids = scope.ServiceProvider.GetRequiredService<SshIdentityService>();
                var (res, identity) = await ids.CreateAsync("id-" + name, linuxUser, privateKey!, null, "test");
                Assert.Equal(IdentityResult2.Ok, res);
                Assert.Equal(IdentityResult2.Ok, await ids.AssignAsync(id, identity!.Id, "test"));
            }
        }

        var http = App.NewClient();
        var login = await TestApp.LoginAsync(http, name, UserPassword);
        Assert.Equal("/", login.Headers.Location?.OriginalString);
        var cookie = login.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("webssh.auth=", StringComparison.Ordinal)).Split(';')[0];
        return new WebUser(id, name, cookie, http);
    }

    public HttpRequestMessage Api(HttpMethod method, string url)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.Add("Origin", Env.Origin);
        r.Headers.Add("X-Requested-With", "WebSsh");
        return r;
    }

    public async Task<Guid> CreateSessionAsync(WebUser u)
    {
        var res = await u.Http.SendAsync(Api(HttpMethod.Post, "/api/sessions"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public Task<WebSocket> ConnectAsync(WebUser u, Guid session, string? origin = "default")
    {
        var client = App.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            r.Headers["Cookie"] = u.Cookie;
            var o = origin == "default" ? Env.Origin : origin;
            if (o is not null) r.Headers["Origin"] = o;
        };
        return client.ConnectAsync(new Uri($"ws://localhost/ws/terminal/{session}"), CancellationToken.None);
    }

    public void Dispose()
    {
        App.Dispose();
        Env.Dispose();
    }
}

public static class WsExtensions
{
    public static Task SendInputAsync(this WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Binary, true, CancellationToken.None);

    public static Task SendResizeAsync(this WebSocket ws, int cols, int rows) =>
        ws.SendAsync(Encoding.UTF8.GetBytes($$"""{"type":"resize","cols":{{cols}},"rows":{{rows}}}"""), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Acumula la sortida fins que conté <paramref name="marker"/> (o falla per timeout).</summary>
    public static async Task<string> ReadUntilAsync(this WebSocket ws, string marker, int seconds = 20)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var sb = new StringBuilder();
        var buffer = new byte[8192];
        try
        {
            while (!sb.ToString().Contains(marker, StringComparison.Ordinal))
            {
                var r = await ws.ReceiveAsync(buffer, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            }
        }
        catch (OperationCanceledException) { }
        Assert.True(sb.ToString().Contains(marker, StringComparison.Ordinal), $"No ha arribat '{marker}'. Rebut: {System.Text.Json.JsonSerializer.Serialize(sb.ToString())}");
        return sb.ToString();
    }
}
