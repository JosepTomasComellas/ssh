using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace WebSshTerminal.Tests;

[Collection("env")]
public sealed partial class MfaTests
{
    private static byte[] Base32Decode(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0; var value = 0; var output = new List<byte>();
        foreach (var ch in s.ToUpperInvariant().Where(alphabet.Contains))
        {
            value = (value << 5) | alphabet.IndexOf(ch);
            bits += 5;
            if (bits >= 8) { output.Add((byte)((value >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return [.. output];
    }

    // HMAC-SHA1 és el que defineix el TOTP estàndard (RFC 6238); aquí només genera codis de prova.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "TOTP RFC 6238 usa HMAC-SHA1")]
    /// <summary>TOTP RFC 6238 (HMAC-SHA1, 30 s, 6 dígits); <paramref name="stepOffset"/> per provar la finestra.</summary>
    private static string Totp(string base32Key, int stepOffset = 0)
    {
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + stepOffset;
        var msg = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        var hash = HMACSHA1.HashData(Base32Decode(base32Key), msg);
        var o = hash[^1] & 0x0F;
        var bin = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (bin % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<(string Key, string[] Recovery)> EnableMfaAsync(HttpClient c)
    {
        await TestApp.PostFormAsync(c, "/account/mfa/start", "/account/mfa", new());
        var page = await c.GetStringAsync("/account/mfa");
        var qr = QrRegex().Match(page);
        Assert.True(qr.Success, "La pàgina de MFA ha de mostrar un QR en línia (SVG)");
        var svg = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(System.Net.WebUtility.HtmlDecode(qr.Groups[1].Value)));
        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("secret=", svg, StringComparison.Ordinal); // el QR és gràfic: la clau no apareix com a text
        var key = KeyRegex().Match(page).Groups[1].Value.Replace(" ", "", StringComparison.Ordinal);
        Assert.NotEmpty(key);
        var confirm = await TestApp.PostFormAsync(c, "/account/mfa/confirm", "/account/mfa", new() { ["code"] = Totp(key) });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var recovery = RecoveryRegex().Matches(await confirm.Content.ReadAsStringAsync()).Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(10, recovery.Length);
        return (key, recovery);
    }

    [Fact]
    public async Task Totp_can_be_enabled_and_is_then_required_at_login()
    {
        using var app = new TestApp();
        string key; string[] recovery; string pw;
        using (var c = app.NewClient())
        {
            pw = await TestApp.LoginAdminAsync(c);
            (key, recovery) = await EnableMfaAsync(c);
        }

        using var again = app.NewClient();
        var first = await TestApp.LoginAsync(again, "admin", pw);
        Assert.Equal("/login/2fa", first.Headers.Location?.OriginalString);
        Assert.NotEqual(HttpStatusCode.OK, (await again.GetAsync("/")).StatusCode); // encara no autenticat

        var wrong = await TestApp.PostFormAsync(again, "/account/2fa", "/login/2fa", new() { ["code"] = "000000" });
        Assert.Equal("/login/2fa?error=1", wrong.Headers.Location?.OriginalString);

        var ok = await TestApp.PostFormAsync(again, "/account/2fa", "/login/2fa", new() { ["code"] = Totp(key) });
        Assert.Equal("/", ok.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/")).StatusCode);
        Assert.NotEmpty(recovery);
    }

    [Fact]
    public async Task Recovery_codes_work_once()
    {
        using var app = new TestApp();
        string pw; string[] recovery;
        using (var c = app.NewClient())
        {
            pw = await TestApp.LoginAdminAsync(c);
            (_, recovery) = await EnableMfaAsync(c);
        }

        using var login1 = app.NewClient();
        await TestApp.LoginAsync(login1, "admin", pw);
        var used = await TestApp.PostFormAsync(login1, "/account/2fa", "/login/2fa", new() { ["recovery"] = recovery[0] });
        Assert.Equal("/", used.Headers.Location?.OriginalString);

        using var login2 = app.NewClient();
        await TestApp.LoginAsync(login2, "admin", pw);
        var reuse = await TestApp.PostFormAsync(login2, "/account/2fa", "/login/2fa", new() { ["recovery"] = recovery[0] });
        Assert.Equal("/login/2fa?error=1", reuse.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Disabling_requires_the_password()
    {
        using var app = new TestApp();
        using var c = app.NewClient();
        var pw = await TestApp.LoginAdminAsync(c);
        await EnableMfaAsync(c);

        var bad = await TestApp.PostFormAsync(c, "/account/mfa/disable", "/account/mfa", new() { ["password"] = "no-es-aquesta-12345" });
        Assert.Contains("error=password", bad.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var good = await TestApp.PostFormAsync(c, "/account/mfa/disable", "/account/mfa", new() { ["password"] = pw });
        Assert.Equal("/account/mfa", good.Headers.Location?.OriginalString);

        using var again = app.NewClient();
        Assert.Equal("/", (await TestApp.LoginAsync(again, "admin", pw)).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task RequireMfa_policy_blocks_everything_until_mfa_is_enabled_and_cannot_be_disabled()
    {
        using var env = new TestEnv { RequireMfa = true };
        using var app = new TestApp(env);
        using var c = app.NewClient();
        var pw = await TestApp.LoginAdminAsync(c, "Admin-New-Password-99");

        var home = await c.GetAsync("/");
        Assert.Equal("/account/mfa", home.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/account/mfa")).StatusCode);

        await EnableMfaAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/")).StatusCode);

        var disable = await TestApp.PostFormAsync(c, "/account/mfa/disable", "/account/mfa", new() { ["password"] = pw });
        Assert.Contains("error=required", disable.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"<img id=""mfa-qr""[^>]*src=""data:image/svg(?:\+|&#x2B;)xml;base64,([^""]+)""")]
    private static partial Regex QrRegex();

    [GeneratedRegex(@"id=""mfa-key"">([^<]+)<")]
    private static partial Regex KeyRegex();

    [GeneratedRegex(@"<li><code>([^<]+)</code></li>")]
    private static partial Regex RecoveryRegex();
}
