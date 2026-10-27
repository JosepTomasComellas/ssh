namespace WebSshTerminal.Infrastructure;

/// <summary>Esdeveniments de log estables. Cap missatge inclou secrets, ordres ni sortida de terminal.</summary>
internal static partial class Log
{
    [LoggerMessage(1000, LogLevel.Information, "Web SSH Terminal iniciat (revisió {Revision}).")]
    public static partial void Started(ILogger logger, string revision);

    [LoggerMessage(1001, LogLevel.Information, "Aturada ordenada sol·licitada.")]
    public static partial void Stopping(ILogger logger);

    [LoggerMessage(1002, LogLevel.Information, "Aturada completada.")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(1100, LogLevel.Critical, "Configuració invàlida: {Problem}")]
    public static partial void InvalidConfiguration(ILogger logger, string problem);

    [LoggerMessage(1300, LogLevel.Warning, "Administrador inicial creat; cal canviar la contrasenya al primer accés.")]
    public static partial void BootstrapCreated(ILogger logger);

    [LoggerMessage(1301, LogLevel.Critical, "No s'ha pogut crear l'administrador inicial (codis: {Codes}).")]
    public static partial void BootstrapFailed(ILogger logger, string codes);

    [LoggerMessage(1310, LogLevel.Information, "Acció d'administració {Action} completada.")]
    public static partial void AdminAction(ILogger logger, string action);

    [LoggerMessage(1311, LogLevel.Warning, "Compte bloquejat per intents fallits.")]
    public static partial void LockedOut(ILogger logger);

    [LoggerMessage(1200, LogLevel.Error, "Error no gestionat. TraceId={TraceId}")]
    public static partial void Unhandled(ILogger logger, Exception exception, string traceId);
}
