using System.Diagnostics;

namespace WebSshTerminal.Tests;

/// <summary>Contenidor OpenSSH efímer (tests/fixtures/ssh): comptes alice i bob, tmux i SFTP, sense sudo.</summary>
public sealed class SshFixture : IAsyncLifetime
{
    public const string Image = "webssh-fixture-ssh:test";

    private string _container = "";
    public int Port { get; private set; }
    public string HostKeyLine { get; private set; } = "";
    public string AlicePrivateKey { get; private set; } = "";
    public string BobPrivateKey { get; private set; } = "";

    /// <summary>known_hosts vàlid per al contenidor (format [host]:port).</summary>
    public string KnownHosts(string host = "127.0.0.1") => $"[{host}]:{Port} {HostKeyLine}\n";

    public async Task InitializeAsync()
    {
        var repo = FindRepoRoot();
        await Docker($"build -q -t {Image} \"{Path.Combine(repo, "tests", "fixtures", "ssh")}\"");
        _container = (await Docker($"run -d --rm -p 127.0.0.1:0:22 {Image}")).Trim();

        // Espera que sshd escolti i que les claus hagin estat generades.
        for (var i = 0; i < 50; i++)
        {
            var (code, _) = await TryDocker($"exec {_container} test -f /keys/bob");
            if (code == 0) break;
            await Task.Delay(200);
        }
        var mapped = (await Docker($"port {_container} 22/tcp")).Trim().Split('\n')[0];
        Port = int.Parse(mapped[(mapped.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        var pub = (await Docker($"exec {_container} cat /etc/ssh/ssh_host_ed25519_key.pub")).Trim().Split(' ');
        HostKeyLine = $"{pub[0]} {pub[1]}";
        AlicePrivateKey = await Docker($"exec {_container} cat /keys/alice");
        BobPrivateKey = await Docker($"exec {_container} cat /keys/bob");

        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync("127.0.0.1", Port);
                break;
            }
            catch (System.Net.Sockets.SocketException) { await Task.Delay(200); }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container.Length > 0) await TryDocker($"rm -f {_container}");
    }

    /// <summary>Executa una ordre dins el contenidor (p. ex. per verificar un fitxer pujat per SFTP).</summary>
    public async Task<string> ExecAsync(string user, string command) =>
        await Docker($"exec -u {user} {_container} sh -c \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"");

    private static async Task<string> Docker(string args)
    {
        var (code, output) = await TryDocker(args);
        if (code != 0) throw new InvalidOperationException($"docker {args.Split(' ')[0]} ha fallat ({code}): {output}");
        return output;
    }

    private static async Task<(int, string)> TryDocker(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("docker", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout + await stderr);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WebSshTerminal.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No es troba l'arrel del repositori.");
    }
}
