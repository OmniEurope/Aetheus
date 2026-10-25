// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>How a Compose file uses a variable. The order is the order of strength: a variable read in
/// several places takes the strongest use.</summary>
internal enum ComposeVariableForm
{
    /// <summary><c>${NAME:-default}</c> or <c>${NAME-default}</c>: the file names its own default.</summary>
    WithDefault,

    /// <summary><c>${NAME:+alternative}</c>: only tested for presence.</summary>
    Alternative,

    /// <summary><c>${NAME}</c> or <c>$NAME</c>: Compose substitutes an empty string, with a warning, when it is unset.</summary>
    Plain,

    /// <summary><c>${NAME:?message}</c> or <c>${NAME?message}</c>: Compose stops when it is unset.</summary>
    Required
}

/// <summary>A variable a Compose file reads, with the default it names when it names one.</summary>
internal sealed record ComposeVariable(string Name, ComposeVariableForm Form, string? Default);

/// <summary>
/// Lists the variables a Compose file interpolates (<c>${NAME}</c>, <c>${NAME:?message}</c>,
/// <c>${NAME:-default}</c>, <c>$NAME</c>), the way Compose reads them: <c>$$</c> is an escaped dollar, a
/// comment line reads nothing. The wizard asks only what the file asks, never a guess (ADR-057).
/// </summary>
internal static partial class ComposeVariableExtractor
{
    private const int MaxVariables = 200;

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?:(:?[-?+])([^}]*))?\}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Braced();

    [GeneratedRegex(@"\$([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Bare();

    public static IReadOnlyList<ComposeVariable> Extract(string compose)
    {
        ArgumentNullException.ThrowIfNull(compose);

        var found = new Dictionary<string, ComposeVariable>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var rawLine in compose.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = WithoutComment(rawLine).Replace("$$", string.Empty, StringComparison.Ordinal);
            if (line.Length == 0) continue;
            foreach (Match match in Braced().Matches(line))
                Add(found, order, new ComposeVariable(
                    match.Groups[1].Value, FormOf(match.Groups[2].Value), match.Groups[2].Success ? match.Groups[3].Value : null));
            foreach (Match match in Bare().Matches(Braced().Replace(line, string.Empty)))
                Add(found, order, new ComposeVariable(match.Groups[1].Value, ComposeVariableForm.Plain, null));
        }

        return [.. order.Take(MaxVariables).Select(name => found[name])];
    }

    private static ComposeVariableForm FormOf(string operatorText) => operatorText switch
    {
        "" => ComposeVariableForm.Plain,
        ":?" or "?" => ComposeVariableForm.Required,
        ":+" or "+" => ComposeVariableForm.Alternative,
        _ => ComposeVariableForm.WithDefault
    };

    private static void Add(Dictionary<string, ComposeVariable> found, List<string> order, ComposeVariable variable)
    {
        if (!found.TryGetValue(variable.Name, out var known))
        {
            found[variable.Name] = variable;
            order.Add(variable.Name);
            return;
        }

        // The strongest use wins; a default is kept from the first place that names one.
        var stronger = variable.Form > known.Form ? variable.Form : known.Form;
        found[variable.Name] = new ComposeVariable(variable.Name, stronger, known.Default ?? variable.Default);
    }

    /// <summary>The line without its YAML comment: a <c>#</c> that opens the line or follows a space,
    /// outside quotes.</summary>
    private static string WithoutComment(string line)
    {
        var result = new StringBuilder(line.Length);
        char? quote = null;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote is null && character == '#' && (index == 0 || char.IsWhiteSpace(line[index - 1])))
                break;
            if (character is '"' or '\'')
                quote = quote is null ? character : quote == character ? null : quote;
            result.Append(character);
        }
        return result.ToString();
    }
}
