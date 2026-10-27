using System.Text.RegularExpressions;
using WebSshTerminal.Configuration;

namespace WebSshTerminal.Infrastructure;

/// <summary>Secrets obligatoris llegits de fitxers muntats. Mai es registren ni es mostren.</summary>
public sealed partial class AppSecrets
{
    public required byte[] AppKey { get; init; }
    public required string BootstrapPassword { get; init; }

    public static AppSecrets Load(WebSshOptions o, ICollection<string> errors)
    {
        var key = ReadFile(o.AppKeyFile, "AppKeyFile", errors)?.Trim();
        byte[] keyBytes = [];
        if (key is not null)
        {
            if (!HexKey().IsMatch(key)) errors.Add("WebSsh:AppKeyFile ha de contenir 64 caràcters hexadecimals.");
            else keyBytes = Convert.FromHexString(key);
        }

        var boot = ReadFile(o.BootstrapPasswordFile, "BootstrapPasswordFile", errors)?.Trim() ?? "";
        ReadFile(o.SshCredentialsFile, "SshCredentialsFile", errors);
        ReadFile(o.KnownHostsFile, "KnownHostsFile", errors);
        return new AppSecrets { AppKey = keyBytes, BootstrapPassword = boot };
    }

    private static string? ReadFile(string path, string name, ICollection<string> errors)
    {
        try
        {
            if (!File.Exists(path)) { errors.Add($"WebSsh:{name}: el fitxer no existeix."); return null; }
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.OtherRead | UnixFileMode.OtherExecute)) != 0)
                    errors.Add($"WebSsh:{name}: permisos massa oberts (no pot ser llegible per 'others').");
            }
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"WebSsh:{name}: no es pot llegir.");
            return null;
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex HexKey();
}
