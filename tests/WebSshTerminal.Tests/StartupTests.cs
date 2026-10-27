using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using WebSshTerminal.Configuration;
using WebSshTerminal.Infrastructure;

namespace WebSshTerminal.Tests;

[Collection("env")]
public class StartupTests
{
    [Fact]
    public async Task Ready_returns_exact_minimal_payload()
    {
        using var env = new TestEnv().Apply();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = factory.CreateClient();

        var res = await client.GetAsync("/health/ready");

        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        Assert.Matches("^[0-9a-f]{40}$", BuildInfo.Revision);
        Assert.Equal($$"""{"status":"ok","revision":"{{BuildInfo.Revision}}"}""", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Live_does_not_redirect()
    {
        using var env = new TestEnv().Apply();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public void Missing_configuration_is_rejected_without_leaking_values()
    {
        var errors = WebSshOptionsValidator.Validate(new WebSshOptions { PublicUrl = "http://secret-host.test" }, requireHttps: true);
        Assert.NotEmpty(errors);
        Assert.DoesNotContain(errors, e => e.Contains("secret-host", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("user@host")]
    [InlineData("host:22")]
    [InlineData("ssh://host")]
    [InlineData("")]
    public void Ssh_host_must_be_a_plain_hostname(string host)
    {
        var o = new WebSshOptions { SshHost = host };
        Assert.Contains(WebSshOptionsValidator.Validate(o, false), e => e.Contains("SshHost"));
    }

    [Fact]
    public void Trusted_proxies_accept_only_exact_ips()
    {
        var errors = new List<string>();
        WebSshOptionsValidator.ParseProxies("10.0.0.0/8, 172.18.0.2", errors);
        Assert.Single(errors);
    }

    [Fact]
    public void Missing_secret_files_fail_closed()
    {
        var errors = new List<string>();
        AppSecrets.Load(new WebSshOptions { AppKeyFile = "/nope/a", BootstrapPasswordFile = "/nope/b", SshCredentialsFile = "/nope/c", KnownHostsFile = "/nope/d" }, errors);
        Assert.Equal(4, errors.Count);
    }

    [Theory]
    [InlineData("1.0.0+0123456789abcdef0123456789abcdef01234567", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("1.0.0", BuildInfo.UnknownRevision)]
    [InlineData(null, BuildInfo.UnknownRevision)]
    public void Revision_is_extracted_from_informational_version(string? input, string expected)
        => Assert.Equal(expected, BuildInfo.Resolve(input));
}
