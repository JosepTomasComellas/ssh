namespace WebSshTerminal.Ssh;

public enum HostKeyVerdict { Trusted, Unknown, Mismatch }

/// <summary>
/// Valida la clau pública del servidor contra un fitxer <c>known_hosts</c> aprovat fora de banda.
/// Suporta noms exactes i <c>[host]:port</c>. No suporta hosts hashejats ni comodins: les línies
/// que no es poden interpretar s'ignoren, de manera que no poden concedir confiança.
/// </summary>
public sealed class KnownHostsVerifier
{
    private sealed record Entry(string Host, int Port, string KeyType, byte[] Key, bool Revoked);

    private readonly List<Entry> _entries = [];

    public static KnownHostsVerifier LoadFile(string path) => Parse(File.ReadAllLines(path));

    public static KnownHostsVerifier Parse(IEnumerable<string> lines)
    {
        var v = new KnownHostsVerifier();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var i = 0;
            var revoked = false;
            if (parts[0].StartsWith('@'))
            {
                if (parts[0] != "@revoked") continue; // @cert-authority no es suporta
                revoked = true;
                i = 1;
            }
            if (parts.Length < i + 3) continue;
            var hosts = parts[i];
            if (hosts.StartsWith('|')) continue; // hashejat: no suportat
            byte[] key;
            try { key = Convert.FromBase64String(parts[i + 2]); }
            catch (FormatException) { continue; }

            foreach (var h in hosts.Split(','))
            {
                if (h.Contains('*') || h.Contains('?') || h.StartsWith('!')) continue;
                var (host, port) = SplitHost(h);
                v._entries.Add(new Entry(host, port, parts[i + 1], key, revoked));
            }
        }
        return v;
    }

    public int Count => _entries.Count;

    public HostKeyVerdict Verify(string host, int port, string keyType, ReadOnlySpan<byte> key)
    {
        var forHost = _entries.Where(e => string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase) && e.Port == port).ToList();
        if (forHost.Count == 0) return HostKeyVerdict.Unknown;

        var trusted = false;
        foreach (var e in forHost)
        {
            if (e.KeyType != keyType || !key.SequenceEqual(e.Key)) continue;
            if (e.Revoked) return HostKeyVerdict.Mismatch;
            trusted = true;
        }
        return trusted ? HostKeyVerdict.Trusted : HostKeyVerdict.Mismatch;
    }

    private static (string Host, int Port) SplitHost(string h)
    {
        if (h.StartsWith('[') && h.IndexOf("]:", StringComparison.Ordinal) is var end and > 0 && int.TryParse(h[(end + 2)..], out var p))
            return (h[1..end], p);
        return (h, 22);
    }
}
