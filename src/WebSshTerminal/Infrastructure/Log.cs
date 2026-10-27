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

    [LoggerMessage(1200, LogLevel.Error, "Error no gestionat. TraceId={TraceId}")]
    public static partial void Unhandled(ILogger logger, Exception exception, string traceId);
}
