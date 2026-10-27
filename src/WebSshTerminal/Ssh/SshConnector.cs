using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using WebSshTerminal.Configuration;
using WebSshTerminal.Data;
using WebSshTerminal.Infrastructure;

namespace WebSshTerminal.Ssh;

public enum SshFailure { HostKeyRejected, AuthFailed, Unreachable, Other }

/// <summary>Error de connexió amb una categoria segura; mai porta detalls del servidor ni de la credencial.</summary>
public sealed class SshConnectException(SshFailure failure) : Exception($"SSH: {failure}")
{
    public SshFailure Failure { get; } = failure;
}

/// <summary>
/// Connecta ÚNICAMENT a l'host configurat. El destí no depèn de cap entrada d'usuari; el nom es resol
/// una vegada i la connexió usa aquella IP (evita DNS rebinding entre validació i connexió).
/// </summary>
public sealed class SshConnector(WebSshOptions options, CredentialProtector protector, ILogger<SshConnector> logger)
{
    private static readonly string[] Kex =
    [
        "curve25519-sha256", "curve25519-sha256@libssh.org", "ecdh-sha2-nistp256", "ecdh-sha2-nistp384", "ecdh-sha2-nistp521",
        "diffie-hellman-group-exchange-sha256", "diffie-hellman-group14-sha256", "diffie-hellman-group16-sha512", "diffie-hellman-group18-sha512",
    ];
    private static readonly string[] HostKeys =
        ["ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "rsa-sha2-256", "rsa-sha2-512"];
    private static readonly string[] Ciphers =
        ["chacha20-poly1305@openssh.com", "aes256-gcm@openssh.com", "aes128-gcm@openssh.com", "aes256-ctr", "aes192-ctr", "aes128-ctr"];
    private static readonly string[] Macs =
        ["hmac-sha2-256-etm@openssh.com", "hmac-sha2-512-etm@openssh.com", "hmac-sha2-256", "hmac-sha2-512"];

    public Task<SshClient> ConnectAsync(SshIdentity identity, CancellationToken ct) =>
        ConnectAsync(identity, info => new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) }, ct);

    public Task<SftpClient> ConnectSftpAsync(SshIdentity identity, CancellationToken ct) =>
        ConnectAsync(identity, info => new SftpClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30), OperationTimeout = TimeSpan.FromSeconds(60) }, ct);

    private async Task<T> ConnectAsync<T>(SshIdentity identity, Func<Renci.SshNet.ConnectionInfo, T> create, CancellationToken ct) where T : BaseClient
    {
        var address = await ResolveAsync(ct);
        var knownHosts = KnownHostsVerifier.LoadFile(options.KnownHostsFile); // es rellegeix: els canvis de l'admin apliquen sense reiniciar
        var keyBytes = protector.Unprotect(identity.ProtectedKey, identity.Id);
        try
        {
            using var keyStream = new MemoryStream(keyBytes, writable: false);
            using var keyFile = new PrivateKeyFile(keyStream);
            var info = new Renci.SshNet.ConnectionInfo(address.ToString(), options.SshPort, identity.LinuxUser, new PrivateKeyAuthenticationMethod(identity.LinuxUser, keyFile))
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
            Restrict(info.KeyExchangeAlgorithms.Keys.ToList(), Kex, k => info.KeyExchangeAlgorithms.Remove(k), "kex");
            Restrict(info.HostKeyAlgorithms.Keys.ToList(), HostKeys, k => info.HostKeyAlgorithms.Remove(k), "hostkey");
            Restrict(info.Encryptions.Keys.ToList(), Ciphers, k => info.Encryptions.Remove(k), "cipher");
            Restrict(info.HmacAlgorithms.Keys.ToList(), Macs, k => info.HmacAlgorithms.Remove(k), "mac");

            var client = create(info);
            var hostKeyRejected = false;
            client.HostKeyReceived += (_, e) =>
            {
                e.CanTrust = knownHosts.Verify(options.SshHost, options.SshPort, e.HostKeyName, e.HostKey) == HostKeyVerdict.Trusted;
                hostKeyRejected |= !e.CanTrust;
            };

            try
            {
                await Task.Run(client.Connect, ct);
                return client;
            }
            catch (Exception ex)
            {
                client.Dispose();
                var failure = hostKeyRejected ? SshFailure.HostKeyRejected
                    : ex is SshAuthenticationException ? SshFailure.AuthFailed
                    : ex is SocketException or SshOperationTimeoutException or SshConnectionException ? SshFailure.Unreachable
                    : SshFailure.Other;
                Log.SshConnectFailed(logger, failure.ToString());
                throw new SshConnectException(failure);
            }
        }
        finally
        {
            Array.Clear(keyBytes);
        }
    }

    private async Task<IPAddress> ResolveAsync(CancellationToken ct)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(options.SshHost, ct); }
        catch (SocketException) { throw new SshConnectException(SshFailure.Unreachable); }

        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        if (ip is null || IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip) || ip.IsIPv6Multicast)
            throw new SshConnectException(SshFailure.Unreachable);
        return ip;
    }

    private static void Restrict(List<string> available, string[] allowed, Action<string> remove, string what)
    {
        foreach (var k in available.Where(k => !allowed.Contains(k))) remove(k);
        if (!available.Any(allowed.Contains))
            throw new InvalidOperationException($"Cap algorisme segur de {what} disponible a la biblioteca SSH.");
    }
}
