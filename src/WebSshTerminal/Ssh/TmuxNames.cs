using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace WebSshTerminal.Ssh;

/// <summary>Els noms de sessió tmux es generen al servidor i es validen abans de tocar cap ordre remota.</summary>
public static partial class TmuxNames
{
    public static string Generate() => "ws-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    public static bool IsValid(string? name) => name is not null && NameRegex().IsMatch(name);

    [GeneratedRegex("^ws-[0-9a-f]{16}$")]
    private static partial Regex NameRegex();
}
