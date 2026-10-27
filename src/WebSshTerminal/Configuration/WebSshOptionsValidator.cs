using System.Net;
using System.Text.RegularExpressions;

namespace WebSshTerminal.Configuration;

/// <summary>Valida la configuració. Els errors mencionen només el nom del camp, mai el valor.</summary>
public static partial class WebSshOptionsValidator
{
    public static IReadOnlyList<string> Validate(WebSshOptions o, bool requireHttps)
    {
        var errors = new List<string>();

        if (!Uri.TryCreate(o.PublicUrl, UriKind.Absolute, out var pub) || pub.PathAndQuery != "/")
            errors.Add("WebSsh:PublicUrl ha de ser una URL absoluta sense ruta.");
        else if (requireHttps && pub.Scheme != Uri.UriSchemeHttps)
            errors.Add("WebSsh:PublicUrl ha de ser https en producció.");

        if (!Uri.TryCreate(o.SourceUrl, UriKind.Absolute, out var src) || src.Scheme != Uri.UriSchemeHttps)
            errors.Add("WebSsh:SourceUrl ha de ser una URL https.");

        if (!HostRegex().IsMatch(o.SshHost))
            errors.Add("WebSsh:SshHost ha de ser un nom d'host o IP sense esquema, usuari ni port.");
        if (o.SshPort is < 1 or > 65535) errors.Add("WebSsh:SshPort fora de rang.");

        foreach (var (name, value) in new[]
        {
            ("DataDirectory", o.DataDirectory), ("KnownHostsFile", o.KnownHostsFile),
            ("SshCredentialsFile", o.SshCredentialsFile), ("AppKeyFile", o.AppKeyFile),
            ("BootstrapPasswordFile", o.BootstrapPasswordFile),
        })
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
                errors.Add($"WebSsh:{name} ha de ser una ruta absoluta.");
        }

        ParseProxies(o.TrustedProxies, errors);
        if (o.MetricsPort is < 0 or > 65535) errors.Add("WebSsh:MetricsPort fora de rang.");
        if (o.MetricsTokenFile.Length > 0 && !Path.IsPathRooted(o.MetricsTokenFile)) errors.Add("WebSsh:MetricsTokenFile ha de ser una ruta absoluta.");

        if (o.MaxSessionsPerUser is < 1 or > 20) errors.Add("WebSsh:MaxSessionsPerUser ha d'estar entre 1 i 20.");
        if (o.MaxTotalSessions < o.MaxSessionsPerUser || o.MaxTotalSessions > 200) errors.Add("WebSsh:MaxTotalSessions fora de rang.");
        if (o.IdleTimeoutMinutes is < 1 or > 1440) errors.Add("WebSsh:IdleTimeoutMinutes fora de rang.");
        if (o.MaxTransferBytes is < 1024 or > 2L * 1024 * 1024 * 1024) errors.Add("WebSsh:MaxTransferBytes fora de rang.");
        if (o.ShutdownTimeoutSeconds is < 1 or > 29) errors.Add("WebSsh:ShutdownTimeoutSeconds ha de ser inferior a 30.");

        if (!ColorRegex().IsMatch(o.Branding.PrimaryColor)) errors.Add("WebSsh:Branding:PrimaryColor ha de ser #RRGGBB.");
        if (o.Branding.Theme is not ("auto" or "light" or "dark")) errors.Add("WebSsh:Branding:Theme ha de ser auto, light o dark.");
        if (string.IsNullOrWhiteSpace(o.Branding.Name) || o.Branding.Name.Length > 60) errors.Add("WebSsh:Branding:Name és obligatori (màx. 60).");
        if (o.Branding.LogoUrl.Length > 0 && !(o.Branding.LogoUrl.StartsWith('/') && !o.Branding.LogoUrl.StartsWith("//")))
            errors.Add("WebSsh:Branding:LogoUrl ha de ser una ruta local que comenci per '/'.");

        return errors;
    }

    public static IReadOnlyList<IPAddress> ParseProxies(string csv, ICollection<string>? errors = null)
    {
        var list = new List<IPAddress>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(part, out var ip)) list.Add(ip);
            else errors?.Add("WebSsh:TrustedProxies conté una IP no vàlida (només IP exactes).");
        }
        return list;
    }

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.\-]{0,251}[A-Za-z0-9])?$")]
    private static partial Regex HostRegex();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorRegex();
}
