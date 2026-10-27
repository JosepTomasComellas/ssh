namespace WebSshTerminal.Configuration;

/// <summary>Configuració de la instància (secció "WebSsh"). Els secrets es llegeixen de fitxers, mai d'aquí.</summary>
public sealed class WebSshOptions
{
    public const string Section = "WebSsh";

    public string PublicUrl { get; set; } = "";
    public string SourceUrl { get; set; } = "https://github.com/JosepTomasComellas/ssh";
    public string DataDirectory { get; set; } = "/var/lib/web-ssh-terminal";

    public string SshHost { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string KnownHostsFile { get; set; } = "";
    public string SshCredentialsFile { get; set; } = "";
    public string AppKeyFile { get; set; } = "";
    public string BootstrapPasswordFile { get; set; } = "";

    /// <summary>IP exactes de proxys de confiança, separades per comes.</summary>
    public string TrustedProxies { get; set; } = "";

    public int MaxSessionsPerUser { get; set; } = 3;
    public int MaxTotalSessions { get; set; } = 20;
    public int IdleTimeoutMinutes { get; set; } = 30;
    public long MaxTransferBytes { get; set; } = 50L * 1024 * 1024;
    public int ShutdownTimeoutSeconds { get; set; } = 25;

    public BrandingOptions Branding { get; set; } = new();
}

/// <summary>Personalització de la instància. El crèdit TuteApps no és configurable.</summary>
public sealed class BrandingOptions
{
    public string Name { get; set; } = "Web SSH Terminal";
    public string PrimaryColor { get; set; } = "#1565c0";
    public string Theme { get; set; } = "auto"; // auto | light | dark
    public string LogoUrl { get; set; } = "";
}
