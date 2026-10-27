using Renci.SshNet;
using Renci.SshNet.Sftp;
using WebSshTerminal.Data;
using WebSshTerminal.Ssh;

namespace WebSshTerminal.Terminal;

public sealed class InvalidSftpPathException(string reason) : Exception(reason);

public static class SftpPaths
{
    public const int MaxLength = 1024;

    /// <summary>
    /// Normalitza una ruta RELATIVA a l'arrel autoritzada (el home del compte remot).
    /// Rebutja absolutes, <c>..</c>, NUL, barres invertides i caràcters de control.
    /// </summary>
    public static string Normalize(string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return "";
        if (relative.Length > MaxLength || relative[0] == '/') throw new InvalidSftpPathException("absolute-or-long");
        foreach (var c in relative)
            if (c == '\\' || char.IsControl(c)) throw new InvalidSftpPathException("bad-char");

        var parts = new List<string>();
        foreach (var seg in relative.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") throw new InvalidSftpPathException("traversal");
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }
}

/// <summary>Sessió SFTP limitada al home del compte remot assignat. El host destí no és configurable per l'usuari.</summary>
public sealed class SftpSession : IDisposable
{
    private readonly SftpClient _client;
    public string Root { get; }

    private SftpSession(SftpClient client)
    {
        _client = client;
        Root = client.WorkingDirectory.TrimEnd('/');
    }

    public SftpClient Client => _client;

    public static async Task<SftpSession> OpenAsync(SshConnector connector, SshIdentity identity, CancellationToken ct) =>
        new(await connector.ConnectSftpAsync(identity, ct));

    /// <summary>Ruta absoluta remota per a una ruta relativa; rebutja enllaços simbòlics als components existents.</summary>
    public string Resolve(string? relative)
    {
        var rel = SftpPaths.Normalize(relative);
        var current = Root;
        if (rel.Length == 0) return current.Length == 0 ? "/" : current;

        foreach (var part in rel.Split('/'))
        {
            // Get()/GetAttributes() fan stat (segueixen enllaços); el llistat del pare dóna els atributs reals.
            var entry = _client.ListDirectory(current.Length == 0 ? "/" : current).FirstOrDefault(e => e.Name == part);
            current = current + "/" + part;
            if (entry is null) continue; // no existeix encara: res a comprovar
            if (entry.IsSymbolicLink) throw new InvalidSftpPathException("symlink");
            if (!entry.IsDirectory) break; // un fitxer no té fills; la resta de la ruta fallarà més endavant
        }
        return current;
    }

    public ISftpFile? TryGet(string absolute) => _client.Exists(absolute) ? _client.Get(absolute) : null;

    public void Dispose() => _client.Dispose();
}
