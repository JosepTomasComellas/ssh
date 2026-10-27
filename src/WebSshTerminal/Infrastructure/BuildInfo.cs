using System.Reflection;
using System.Text.RegularExpressions;

namespace WebSshTerminal.Infrastructure;

public static partial class BuildInfo
{
    public const string UnknownRevision = "0000000000000000000000000000000000000000";

    /// <summary>Commit de 40 caràcters injectat per SourceRevisionId; zeros en compilacions locals.</summary>
    public static string Revision { get; } = Resolve(
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static string Resolve(string? informationalVersion)
    {
        var m = informationalVersion is null ? null : RevisionRegex().Match(informationalVersion);
        return m is { Success: true } ? m.Groups[1].Value.ToLowerInvariant() : UnknownRevision;
    }

    [GeneratedRegex(@"\+([0-9a-fA-F]{40})$")]
    private static partial Regex RevisionRegex();
}
