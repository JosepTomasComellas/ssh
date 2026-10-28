using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace WebSshTerminal.E2E;

[CollectionDefinition("e2e")]
public sealed class E2ECollection : ICollectionFixture<Fixture>;

[Collection("e2e")]
public sealed class E2ETests(Fixture fx)
{
    private async Task<(IBrowserContext Ctx, IPage Page)> LoginAsync(string user, string password, int w = 1280, int h = 800)
    {
        var ctx = await fx.NewContextAsync(w, h);
        var page = await ctx.NewPageAsync();
        await fx.LoginAsync(page, user, password);
        await Waits.UrlAsync(page, u => !u.Contains("/login", StringComparison.Ordinal));
        return (ctx, page);
    }

    private static async Task<string> TerminalText(IPage page) => await page.Locator(".xterm-rows").InnerTextAsync();

    private static async Task WaitForTerminal(IPage page, string text, int ms = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        string last = "";
        while (DateTime.UtcNow < deadline)
        {
            last = await TerminalText(page);
            if (last.Contains(text, StringComparison.Ordinal)) return;
            await Task.Delay(200);
        }
        Assert.Fail($"No ha aparegut «{text}» al terminal. Contingut: {last[..Math.Min(400, last.Length)]}");
    }

    private static async Task Type(IPage page, string command)
    {
        await page.Locator("#terminal").ClickAsync();
        await page.Keyboard.TypeAsync(command);
        await page.Keyboard.PressAsync("Enter");
    }

    private static async Task NewSession(IPage page)
    {
        await page.GotoAsync("/terminal");
        await page.ClickAsync("#btn-new");
        await Assertions.Expect(page.Locator("#status")).ToHaveTextAsync("Connectat", new() { Timeout = 30000 });
        await WaitForTerminal(page, "bash*");
    }

    [Fact]
    public async Task Login_errors_are_announced_and_logout_works()
    {
        var ctx = await fx.NewContextAsync();
        var page = await ctx.NewPageAsync();
        await fx.LoginAsync(page, "alice-web", "contrasenya-incorrecta");
        await Assertions.Expect(page.Locator("[role=alert]")).ToBeVisibleAsync();

        await fx.LoginAsync(page, "alice-web", fx.AlicePassword);
        await Waits.UrlAsync(page, u => !u.Contains("/login", StringComparison.Ordinal));
        await page.ClickAsync("button:text('Surt')");
        await Waits.UrlAsync(page, u => u.EndsWith("/login", StringComparison.Ordinal));
        await page.GotoAsync("/terminal");
        await Waits.UrlAsync(page, u => u.Contains("/login", StringComparison.Ordinal)); // sense sessió no s'hi pot entrar
        await ctx.CloseAsync();
    }

    [Fact]
    public async Task Terminal_runs_commands_resizes_and_reattaches_tmux()
    {
        var (ctx, page) = await LoginAsync("alice-web", fx.AlicePassword);
        await NewSession(page);

        await Type(page, "printf 'user-%s\\n' \"$(whoami)\"");
        await WaitForTerminal(page, "user-alice");

        await Type(page, "export MARK=e2e-persist");
        await page.SetViewportSizeAsync(700, 800);
        await Task.Delay(800);
        await Type(page, "printf 'cols-%s\\n' \"$(stty size | cut -d' ' -f2)\"");
        var text = await WaitForTerminalMatch(page, new Regex(@"cols-(\d+)"));
        Assert.True(int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) < 100, "El resize no ha arribat al PTY");

        await page.ClickAsync("#btn-disconnect");
        await Assertions.Expect(page.Locator("#status")).ToHaveTextAsync("Desconnectat");
        await page.ClickAsync("#btn-connect");
        await Assertions.Expect(page.Locator("#status")).ToHaveTextAsync("Connectat", new() { Timeout = 30000 });
        await WaitForTerminal(page, "bash*");
        await Type(page, "printf 'got-%s\\n' \"$MARK\"");
        await WaitForTerminal(page, "got-e2e-persist");

        await page.ScreenshotAsync(new() { Path = Path.Combine(Fixture.ResultsDir, "terminal-wide.png") });
        await ctx.CloseAsync();
    }

    private static async Task<string> WaitForTerminalMatch(IPage page, Regex re)
    {
        for (var i = 0; i < 100; i++)
        {
            var m = re.Matches(await TerminalText(page));
            if (m.Count > 0) return m[^1].Groups[1].Value;
            await Task.Delay(200);
        }
        Assert.Fail("No ha aparegut el patró esperat");
        return "";
    }

    [Fact]
    public async Task Users_are_isolated_from_each_other_and_from_admin_pages()
    {
        var (aliceCtx, alice) = await LoginAsync("alice-web", fx.AlicePassword);
        await NewSession(alice);
        var aliceId = (await alice.Locator("#session-select").InputValueAsync());

        var (bobCtx, bob) = await LoginAsync("bob-web", fx.BobPassword);
        await bob.GotoAsync("/terminal");

        // Bob no veu la sessió d'Alice a l'API...
        var list = await bob.EvaluateAsync<string>("() => fetch('/api/sessions', { credentials: 'same-origin' }).then(r => r.text())");
        Assert.DoesNotContain(aliceId, list, StringComparison.Ordinal);
        // ...ni pot tancar-la...
        var delStatus = await bob.EvaluateAsync<int>($"() => fetch('/api/sessions/{aliceId}', {{ method: 'DELETE', headers: {{ 'X-Requested-With': 'WebSsh' }}, credentials: 'same-origin' }}).then(r => r.status)");
        Assert.Equal(404, delStatus);
        // ...ni connectar-s'hi des del navegador.
        var outcome = await bob.EvaluateAsync<string>($@"() => new Promise(r => {{
            const ws = new WebSocket('wss://' + location.host + '/ws/terminal/{aliceId}');
            ws.onopen = () => {{ ws.close(); r('open'); }};
            ws.onerror = () => r('refused');
        }})");
        Assert.Equal("refused", outcome);

        // Ni Bob ni Alice són administradors.
        await bob.GotoAsync("/admin/users");
        Assert.Contains("/denied", bob.Url, StringComparison.Ordinal);
        await aliceCtx.CloseAsync();
        await bobCtx.CloseAsync();
    }

    [Fact]
    public async Task Websocket_from_a_foreign_origin_is_refused()
    {
        var (ctx, page) = await LoginAsync("alice-web", fx.AlicePassword);
        await NewSession(page);
        var id = await page.Locator("#session-select").InputValueAsync();
        var cookies = await ctx.CookiesAsync();
        var cookie = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        var proxy = Environment.GetEnvironmentVariable("PROXY_HOST") ?? "nginx";

        async Task<string> TryConnect(string? origin)
        {
            using var ws = new ClientWebSocket();
            ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            ws.Options.SetRequestHeader("Cookie", cookie);
            if (origin is not null) ws.Options.SetRequestHeader("Origin", origin);
            try
            {
                await ws.ConnectAsync(new Uri($"wss://{proxy}:8443/ws/terminal/{id}"), CancellationToken.None);
                return "open";
            }
            catch (WebSocketException) { return "refused"; }
        }

        Assert.Equal("refused", await TryConnect("https://evil.example"));
        Assert.Equal("refused", await TryConnect(null));
        await ctx.CloseAsync();
    }

    [Fact]
    public async Task Sftp_upload_overwrite_confirmation_and_download()
    {
        var (ctx, page) = await LoginAsync("alice-web", fx.AlicePassword);
        await page.GotoAsync("/terminal");
        var name = $"e2e-{Guid.NewGuid():N}.txt";

        await page.SetInputFilesAsync("#upload-file", new FilePayload { Name = name, MimeType = "text/plain", Buffer = "contingut-e2e"u8.ToArray() });
        await Assertions.Expect(page.Locator("#files-status")).ToHaveTextAsync("Fitxer pujat.");
        await Assertions.Expect(page.Locator($"#files-list a:text-is('{name}')")).ToBeVisibleAsync();

        await page.SetInputFilesAsync("#upload-file", new FilePayload { Name = name, MimeType = "text/plain", Buffer = "segon"u8.ToArray() });
        await Assertions.Expect(page.Locator("#overwrite-box")).ToBeVisibleAsync();
        await page.ClickAsync("#btn-overwrite");
        await Assertions.Expect(page.Locator("#files-status")).ToHaveTextAsync("Fitxer pujat.");

        var download = await page.RunAndWaitForDownloadAsync(() => page.ClickAsync($"#files-list a:text-is('{name}')"));
        var path = await download.PathAsync();
        Assert.Equal("segon", await File.ReadAllTextAsync(path!));

        // Ruta invàlida: error accessible, sense efecte.
        await page.FillAsync("#files-path", "../bob");
        await page.ClickAsync("#btn-files-list");
        await Assertions.Expect(page.Locator("#files-status")).ToHaveTextAsync("Ruta no permesa.");
        await ctx.CloseAsync();
    }

    [Theory]
    [InlineData(1280, 800, "wide")]
    [InlineData(390, 844, "narrow")]
    public async Task Credit_links_and_keyboard_navigation_work_without_covering_the_terminal(int w, int h, string label)
    {
        var (ctx, page) = await LoginAsync("alice-web", fx.AlicePassword, w, h);
        await page.GotoAsync("/terminal");

        var credit = page.Locator("a.tuteapps-credit");
        await Assertions.Expect(credit).ToBeVisibleAsync();
        await Assertions.Expect(credit.Locator("img")).ToHaveAttributeAsync("alt", "TuteApps");
        await Assertions.Expect(credit).ToHaveAttributeAsync("title", "Fet per TuteApps");
        Assert.Equal("\"Fet per TuteApps\"", await credit.EvaluateAsync<string>("e => getComputedStyle(e, '::after').content"));

        // Posició: a baix a la dreta del viewport i sense tapar el terminal.
        var box = (await credit.BoundingBoxAsync())!;
        Assert.True(box.X + box.Width > w - 40 && box.Y + box.Height > h - 40, "El crèdit ha d'estar a baix a la dreta");
        await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
        var term = (await page.Locator("#terminal").BoundingBoxAsync())!;
        var creditNow = (await credit.BoundingBoxAsync())!;
        var overlap = term.X < creditNow.X + creditNow.Width && term.X + term.Width > creditNow.X
                      && term.Y < creditNow.Y + creditNow.Height && term.Y + term.Height > creditNow.Y;
        Assert.False(overlap, "El crèdit tapa el terminal");

        // Teclat: sense trampes. Amb «Tab mou el focus» es pot sortir del terminal fins al crèdit.
        await page.ClickAsync("#btn-tabfocus");
        await page.Locator("#terminal textarea").FocusAsync(); // pitjor cas: el focus és dins xterm
        // Teclat: es pot arribar al crèdit amb Tab i mostra el tooltip amb el focus.
        for (var i = 0; i < 60; i++)
        {
            await page.Keyboard.PressAsync("Tab");
            if (await credit.EvaluateAsync<bool>("e => e === document.activeElement")) break;
        }
        Assert.True(await credit.EvaluateAsync<bool>("e => e === document.activeElement"), "Tab no arriba al crèdit");
        await Task.Delay(400); // transició CSS del tooltip
        Assert.Equal("1", await credit.EvaluateAsync<string>("e => getComputedStyle(e, '::after').opacity"));

        // Enllaç al codi i a la llicència.
        await Assertions.Expect(page.Locator("footer a:text('Codi font')")).ToBeVisibleAsync();
        var licence = await page.Locator("footer a:text('Llicència AGPL-3.0')").GetAttributeAsync("href");
        Assert.EndsWith("/LICENSE", licence, StringComparison.Ordinal);

        await page.ScreenshotAsync(new() { Path = Path.Combine(Fixture.ResultsDir, $"terminal-{label}.png"), FullPage = true });
        await ctx.CloseAsync();
    }
}
