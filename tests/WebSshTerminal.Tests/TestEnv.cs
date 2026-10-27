namespace WebSshTerminal.Tests;

/// <summary>Entorn aïllat per a una instància: directori de dades, secrets i configuració per variables d'entorn.</summary>
public sealed class TestEnv : IDisposable
{
    private static readonly string[] Keys =
    [
        "PublicUrl", "SshHost", "SshPort", "DataDirectory", "AppKeyFile", "BootstrapPasswordFile",
        "SshCredentialsFile", "KnownHostsFile", "MaxSessionsPerUser", "MaxTotalSessions", "IdleTimeoutMinutes", "MaxTransferBytes",
    ];

    public string Dir { get; } = Directory.CreateTempSubdirectory("webssh-").FullName;
    public string PublicUrl { get; set; } = "https://ssh.example.test";
    public string SshHost { get; set; } = "ssh-fixture";
    public int SshPort { get; set; } = 22;
    public string KnownHosts { get; set; } = "";
    public int MaxSessionsPerUser { get; set; } = 3;
    public int MaxTotalSessions { get; set; } = 20;
    public int IdleTimeoutMinutes { get; set; } = 30;
    public long MaxTransferBytes { get; set; } = 50L * 1024 * 1024;

    public string Origin => new Uri(PublicUrl).GetLeftPart(UriPartial.Authority);

    private string F(string name, string content)
    {
        var p = Path.Combine(Dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    public Dictionary<string, string?> Settings() => new()
    {
        ["PublicUrl"] = PublicUrl,
        ["SshHost"] = SshHost,
        ["SshPort"] = SshPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["DataDirectory"] = Dir,
        ["AppKeyFile"] = F("app_key", new string('a', 64)),
        ["BootstrapPasswordFile"] = F("bootstrap", TestApp.BootstrapPassword),
        ["SshCredentialsFile"] = F("ssh_credentials", "{}"),
        ["KnownHostsFile"] = F("known_hosts", KnownHosts),
        ["MaxSessionsPerUser"] = MaxSessionsPerUser.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["MaxTotalSessions"] = MaxTotalSessions.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["IdleTimeoutMinutes"] = IdleTimeoutMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["MaxTransferBytes"] = MaxTransferBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Program llegeix la configuració en crear el builder: cal passar-la per entorn.</summary>
    public TestEnv Apply()
    {
        foreach (var (k, v) in Settings()) Environment.SetEnvironmentVariable("WebSsh__" + k, v);
        return this;
    }

    public void Dispose()
    {
        foreach (var k in Keys) Environment.SetEnvironmentVariable("WebSsh__" + k, null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // allibera webssh.db abans d'esborrar
        Directory.Delete(Dir, recursive: true);
    }
}
