using Prometheus;

namespace WebSshTerminal.Infrastructure;

/// <summary>
/// Mètriques AGREGADES de producte. Regla: cap etiqueta ve d'entrada d'usuari ni identifica persones,
/// hosts, sessions, ordres o fitxers; els valors d'etiqueta són conjunts tancats definits aquí.
/// </summary>
public static class AppMetrics
{
    public static readonly Gauge ActiveSessions = Metrics.CreateGauge(
        "webssh_terminal_sessions_active", "Ponts de terminal WebSocket actualment connectats.");

    public static readonly Counter SshConnections = Metrics.CreateCounter(
        "webssh_ssh_connections_total", "Connexions SSH/SFTP al servidor remot segons resultat.",
        new CounterConfiguration { LabelNames = ["result"] });

    public static readonly Counter Attaches = Metrics.CreateCounter(
        "webssh_terminal_attaches_total", "Connexions a un terminal: noves o represes (reconnexió).",
        new CounterConfiguration { LabelNames = ["kind"] });

    public static readonly Histogram SessionDuration = Metrics.CreateHistogram(
        "webssh_terminal_attach_duration_seconds", "Durada de cada connexió de terminal.",
        new HistogramConfiguration { Buckets = [5, 30, 120, 600, 1800, 3600, 14400, 43200] });

    public static readonly Counter Detaches = Metrics.CreateCounter(
        "webssh_terminal_detaches_total", "Finals de connexió de terminal segons motiu.",
        new CounterConfiguration { LabelNames = ["reason"] });

    public static readonly Counter Rejections = Metrics.CreateCounter(
        "webssh_rejections_total", "Peticions rebutjades per seguretat o límits, per categoria.",
        new CounterConfiguration { LabelNames = ["category"] });

    public static readonly Counter Logins = Metrics.CreateCounter(
        "webssh_logins_total", "Intents d'inici de sessió web segons resultat.",
        new CounterConfiguration { LabelNames = ["result"] });

    public static readonly Counter Transfers = Metrics.CreateCounter(
        "webssh_sftp_transfers_total", "Transferències de fitxers per direcció i resultat.",
        new CounterConfiguration { LabelNames = ["direction", "result"] });

    public static readonly Counter TransferBytes = Metrics.CreateCounter(
        "webssh_sftp_bytes_total", "Bytes transferits per SFTP per direcció.",
        new CounterConfiguration { LabelNames = ["direction"] });
}
