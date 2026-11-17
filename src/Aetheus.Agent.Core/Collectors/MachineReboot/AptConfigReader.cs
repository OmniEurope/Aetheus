// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Agent.Core.Collectors.MachineReboot;

/// <summary>One apt option as the last file that sets it left it.</summary>
internal sealed record AptOption(string Value, string File);

/// <summary>
/// Reads the apt configuration the way apt does for what the reboot reader needs: <c>/etc/apt/apt.conf.d/*</c> in name order
/// (only names made of letters, digits, <c>-</c>, <c>_</c> and <c>.</c>, without an extension or with <c>.conf</c>), then
/// <c>/etc/apt/apt.conf</c>; the last assignment of an option wins; option names are case-insensitive; both
/// <c>Unattended-Upgrade::Automatic-Reboot "true";</c> and the braced form are understood. Comments are <c>//</c>, <c>/* */</c>
/// and <c>#</c>. A value the text does not settle is simply absent from the result.
/// </summary>
internal static class AptConfigReader
{
    internal const string PartsDirectory = "/etc/apt/apt.conf.d";
    internal const string MainFile = "/etc/apt/apt.conf";

    public static Dictionary<string, AptOption> Read(IMachineRebootHostFiles files)
    {
        var options = new Dictionary<string, AptOption>(StringComparer.OrdinalIgnoreCase);
        var paths = files.ListFiles(PartsDirectory).Where(IsConfigFileName).Append(MainFile);
        foreach (var path in paths)
        {
            if (files.ReadText(path) is not { } text) continue;
            foreach (var (name, value) in Parse(text)) options[name] = new AptOption(value, path);
        }

        return options;
    }

    /// <summary>apt's own reading of a boolean: <c>null</c> when the value is none of its spellings, so the default applies.</summary>
    public static bool? AsBoolean(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "yes" or "true" or "with" or "on" or "enable" or "1" => true,
        "no" or "false" or "without" or "off" or "disable" or "0" => false,
        _ => null
    };

    private static bool IsConfigFileName(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) return false;
        return !name.Contains('.') || name.EndsWith(".conf", StringComparison.Ordinal);
    }

    /// <summary>The <c>(full name, value)</c> pairs of a configuration text, in order; a braced scope prefixes the names it holds
    /// with <c>::</c>.</summary>
    internal static List<(string Name, string Value)> Parse(string text)
    {
        List<(string, string)> result = [];
        List<string> scope = [];
        List<string> tokens = [];
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) i++;
            else if (IsLineComment(text, i)) i = SkipLine(text, i);
            else if (c == '/' && NextIs(text, i, '*')) i = SkipBlockComment(text, i);
            else if (c == '"') tokens.Add(ReadQuoted(text, ref i));
            else if (c is '{' or '}' or ';')
            {
                ApplyStructure(c, scope, tokens, result);
                i++;
            }
            else tokens.Add(ReadWord(text, ref i));
        }

        return result;
    }

    private static bool NextIs(string text, int i, char expected) => i + 1 < text.Length && text[i + 1] == expected;

    private static bool IsLineComment(string text, int i) => text[i] == '#' || (text[i] == '/' && NextIs(text, i, '/'));

    private static int SkipLine(string text, int i)
    {
        while (i < text.Length && text[i] != '\n') i++;
        return i;
    }

    private static int SkipBlockComment(string text, int i)
    {
        var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return end < 0 ? text.Length : end + 2;
    }

    private static string ReadWord(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not (';' or '{' or '}' or '"')) i++;
        return text[start..i];
    }

    private static void ApplyStructure(char c, List<string> scope, List<string> tokens, List<(string, string)> result)
    {
        if (c == '{') scope.Add(tokens.Count > 0 ? tokens[0] : string.Empty);
        else if (c == '}' && scope.Count > 0) scope.RemoveAt(scope.Count - 1);
        else if (c == ';' && tokens.Count == 2) result.Add((FullName(scope, tokens[0]), tokens[1]));
        tokens.Clear();
    }

    private static string FullName(List<string> scope, string name) =>
        string.Join("::", scope.Append(name).Where(part => part.Length > 0));

    private static string ReadQuoted(string text, ref int i)
    {
        var value = new StringBuilder();
        i++;
        while (i < text.Length && text[i] != '"')
        {
            if (text[i] == '\\' && i + 1 < text.Length) i++;
            value.Append(text[i]);
            i++;
        }

        i++;
        return value.ToString();
    }
}
