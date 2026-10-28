using System.Globalization;
using System.Text;

namespace WebSshTerminal.Configuration;

/// <summary>
/// Resol el branding efectiu. Els fitxers de <c>&lt;dades&gt;/branding/</c> (logo.png, symbol.png, background.jpg|png)
/// substitueixen els de sèrie sense recompilar. Els colors ja han passat la validació (#RRGGBB).
/// </summary>
public sealed class BrandingService
{
    public const string InstanceRequestPath = "/instance-branding";

    public string AppName { get; }
    public string OrgName { get; }
    public string OrgDept { get; }
    public string PrimaryColor { get; }
    public string PrimaryDark { get; }
    public string NavColor { get; }
    public string Theme { get; }
    public string LogoUrl { get; }
    public string SymbolUrl { get; }
    public string BackgroundUrl { get; }
    public string InstanceDirectory { get; }

    public BrandingService(WebSshOptions options)
    {
        var b = options.Branding;
        AppName = b.Name;
        OrgName = b.OrgName;
        OrgDept = b.OrgDept;
        PrimaryColor = b.PrimaryColor;
        PrimaryDark = Shade(b.PrimaryColor, 0.82);
        NavColor = b.NavColor;
        Theme = b.Theme;
        InstanceDirectory = Path.Combine(options.DataDirectory, "branding");

        string Pick(string file, string fallback) =>
            File.Exists(Path.Combine(InstanceDirectory, file)) ? $"{InstanceRequestPath}/{file}" : fallback;

        LogoUrl = b.LogoUrl.Length > 0 ? b.LogoUrl : Pick("logo.png", "/branding/salesians-logo-white.png");
        SymbolUrl = Pick("symbol.png", "/branding/salesians-symbol.png");
        BackgroundUrl = File.Exists(Path.Combine(InstanceDirectory, "background.jpg")) ? $"{InstanceRequestPath}/background.jpg"
            : File.Exists(Path.Combine(InstanceDirectory, "background.png")) ? $"{InstanceRequestPath}/background.png"
            : "/branding/salesians-background.jpg";
    }

    /// <summary>Variables CSS de marca (també les de MudBlazor perquè els seus components hi coincideixin).</summary>
    public string CssVariables()
    {
        var sb = new StringBuilder(":root{");
        sb.Append(CultureInfo.InvariantCulture, $"--brand:{PrimaryColor};--brand-dark:{PrimaryDark};--nav:{NavColor};");
        sb.Append(CultureInfo.InvariantCulture, $"--mud-palette-primary:{PrimaryColor};--mud-palette-primary-darken:{PrimaryDark};");
        sb.Append(CultureInfo.InvariantCulture, $"--mud-palette-appbar-background:{NavColor};");
        sb.Append(CultureInfo.InvariantCulture, $"--bg-image:url('{BackgroundUrl}');");
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Multiplica cada canal per <paramref name="factor"/> (&lt;1 enfosqueix).</summary>
    public static string Shade(string hex, double factor)
    {
        var v = hex.TrimStart('#');
        if (v.Length != 6) return hex;
        int Part(int i) => (int)Math.Clamp(Convert.ToInt32(v.Substring(i, 2), 16) * factor, 0, 255);
        return $"#{Part(0):X2}{Part(2):X2}{Part(4):X2}";
    }
}
