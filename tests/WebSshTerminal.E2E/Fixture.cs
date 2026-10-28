using System.Security.Cryptography;
using System.Text;

using Microsoft.Playwright;

namespace WebSshTerminal.E2E;

/// <summary>Espera per sondeig de l'URL: no depèn de l'estat de càrrega del document (que en arrencades fredes pot trigar).</summary>
public static class Waits
{
    public static async Task UrlAsync(IPage page, Func<string, bool> predicate, int timeoutMs = 60000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(page.Url)) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"URL esperada no assolida; URL actual: {page.Url}");
    }
}

/// <summary>Prepara (de manera idempotent) l'admin, les identitats SSH del fixture i dos usuaris web.</summary>
public sealed class Fixture : IAsyncLifetime
{
    public static string BaseUrl => Environment.GetEnvironmentVariable("BASE_URL") ?? "https://localhost:8443";
    public static string ResultsDir => Environment.GetEnvironmentVariable("RESULTS_DIR") ?? "/results";
    static string Env(string n) => Environment.GetEnvironmentVariable(n) ?? throw new InvalidOperationException($"Falta {n}");

    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;
    public string Bootstrap { get; private set; } = "";
    public string AdminPassword { get; private set; } = "";
    public string AlicePassword { get; private set; } = "";
    public string BobPassword { get; private set; } = "";

    // Contrasenyes deterministes derivades del secret de bootstrap: la prova és repetible sense guardar res.
    private static string Derive(string secret, string who) =>
        "E2e-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret + "|" + who)))[..28];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(ResultsDir);
        Bootstrap = (await File.ReadAllTextAsync(Env("BOOTSTRAP_PASSWORD_FILE"))).Trim();
        AdminPassword = Derive(Bootstrap, "admin");
        AlicePassword = Derive(Bootstrap, "alice-web");
        BobPassword = Derive(Bootstrap, "bob-web");

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        // «localhost:8443» és el proxy (nginx|traefik) dins la xarxa Docker.
        var proxy = Environment.GetEnvironmentVariable("PROXY_HOST") ?? "nginx";
        Browser = await Playwright.Chromium.LaunchAsync(new()
        {
            Args = [$"--host-resolver-rules=MAP localhost {proxy}", "--ignore-certificate-errors"],
        });

        await using var ctx = await NewContextAsync();
        var page = await ctx.NewPageAsync();

        try { await LoginAdminAsync(page); }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            var text = await page.InnerTextAsync("body").ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : "(sense cos)");
            throw new InvalidOperationException($"Login admin: {ex.GetType().Name} a {page.Url} :: {text[..Math.Min(300, text.Length)]}", ex);
        }
        await EnsureIdentityAsync(page, "e2e-alice", "alice");
        await EnsureIdentityAsync(page, "e2e-bob", "bob");
        await EnsureWebUserAsync(page, "alice-web", "e2e-alice", AlicePassword);
        await EnsureWebUserAsync(page, "bob-web", "e2e-bob", BobPassword);

        // Sessions d'execucions anteriors: es tanquen per no arribar al límit per usuari.
        foreach (var (user, pw) in new[] { ("alice-web", AlicePassword), ("bob-web", BobPassword) })
        {
            await using var c = await NewContextAsync();
            var p = await c.NewPageAsync();
            await LoginAsync(p, user, pw);
            await p.GotoAsync("/terminal");
            await p.EvaluateAsync("""
                async () => {
                    const list = await (await fetch('/api/sessions', { credentials: 'same-origin' })).json();
                    for (const s of list) await fetch('/api/sessions/' + s.id, { method: 'DELETE', headers: { 'X-Requested-With': 'WebSsh' }, credentials: 'same-origin' });
                }
                """);
        }
    }

    public async Task DisposeAsync()
    {
        await Browser.CloseAsync();
        Playwright.Dispose();
    }

    public async Task<IBrowserContext> NewContextAsync(int width = 1280, int height = 800)
    {
        var ctx = await Browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, ViewportSize = new() { Width = width, Height = height }, BaseURL = BaseUrl });
        ctx.SetDefaultTimeout(60000); // arrencades fredes (runners de CI) poden trigar més de 30 s
        ctx.SetDefaultNavigationTimeout(60000);
        return ctx;
    }

    public async Task LoginAsync(IPage page, string user, string password)
    {
        await page.GotoAsync("/login");
        await page.FillAsync("#username", user);
        await page.FillAsync("#password", password);
        // Èxit → fora de /login; error → /login?error=1. Espera sempre una navegació nova.
        await page.ClickAsync("form[action='/account/login'] button[type=submit]");
        await Waits.UrlAsync(page, u => u != BaseUrl + "/login");
    }

    private async Task LoginAdminAsync(IPage page)
    {
        var trail = new List<string>();
        await LoginAsync(page, "admin", AdminPassword);
        trail.Add("1:" + page.Url);
        if (page.Url.Contains("error=1", StringComparison.Ordinal))
        {
            await LoginAsync(page, "admin", Bootstrap); // primera execució: canvi obligatori
            trail.Add("2:" + page.Url);
            await ChangePasswordAsync(page, Bootstrap, AdminPassword);
            trail.Add("3:" + page.Url);
        }
        if (page.Url.Contains("/login", StringComparison.Ordinal)) Assert.Fail("Trail: " + string.Join(" | ", trail));
        if (page.Url.Contains("/login", StringComparison.Ordinal))
            Assert.Fail($"Login admin fallit: {page.Url} :: {(await page.InnerTextAsync("body"))[..Math.Min(300, (await page.InnerTextAsync("body")).Length)]}");
    }

    public static async Task ChangePasswordAsync(IPage page, string current, string next)
    {
        await Waits.UrlAsync(page, u => u.EndsWith("/password", StringComparison.Ordinal));
        await page.FillAsync("#current", current);
        await page.FillAsync("#next", next);
        await page.FillAsync("#confirm", next);
        await page.ClickAsync("form[action='/account/password'] button[type=submit]");
        await Waits.UrlAsync(page, u => !u.Contains("/password", StringComparison.Ordinal));
    }

    private static async Task EnsureIdentityAsync(IPage page, string name, string linuxUser)
    {
        await page.GotoAsync("/admin/identities");
        if (await page.Locator($"td:text-is('{name}')").CountAsync() > 0) return;
        await page.FillAsync("#name", name);
        await page.FillAsync("#linuxUser", linuxUser);
        await page.FillAsync("#privateKey", await File.ReadAllTextAsync(Path.Combine(Env("FIXTURE_KEYS_DIR"), linuxUser)));
        await page.ClickAsync("button:text('Desa la identitat')");
        await page.WaitForSelectorAsync(".form-ok");
    }

    private async Task EnsureWebUserAsync(IPage admin, string user, string identity, string finalPassword)
    {
        await admin.GotoAsync("/admin/users");
        var row = admin.Locator($"tr:has(td:text-is('{user}'))");
        if (await row.CountAsync() == 0)
        {
            await admin.FillAsync("#username", user);
            await admin.ClickAsync("button:text('Crea')");
        }
        else
        {
            await row.Locator("button:text('Reinicia contrasenya')").ClickAsync(); // temp nova: sabem entrar-hi
        }
        var temp = await admin.Locator("#temp").InnerTextAsync();
        await admin.GotoAsync("/admin/users");
        var assign = admin.Locator($"tr:has(td:text-is('{user}'))");
        await assign.Locator("select[name=identityId]").SelectOptionAsync(new SelectOptionValue { Label = $"{identity} ({(user.StartsWith("alice", StringComparison.Ordinal) ? "alice" : "bob")})" });
        await assign.Locator("button:text('Assigna')").ClickAsync();

        await using var ctx = await NewContextAsync();
        var page = await ctx.NewPageAsync();
        await LoginAsync(page, user, temp);
        await ChangePasswordAsync(page, temp, finalPassword);
    }

}
