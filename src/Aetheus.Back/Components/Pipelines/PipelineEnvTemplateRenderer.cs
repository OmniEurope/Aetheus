// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Renders a versioned environment sample (<c>KEY=VALUE</c> lines whose values carry <c>#{NAME}#</c>
/// placeholders, never a secret) into the dotenv file a Compose run reads with <c>--env-file</c>
/// (ADR-057). Every value is written in double quotes with <c>\</c>, <c>"</c> and <c>$</c> escaped, so
/// a generated password, a URL or a value with spaces reaches Compose exactly as stored. Comments and
/// blank lines pass through; a placeholder with no value, a malformed line, a line break inside a value
/// or a left-over <c>#{</c> fails the rendering instead of producing a file that would start the
/// application with an empty setting.
/// </summary>
internal static class PipelineEnvTemplateRenderer
{
    private const int MaxRenderedLength = 256 * 1024;
    private static readonly Regex TokenRegex = new(
        @"#\{([A-Za-z_][A-Za-z0-9_.-]*)\}#",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex KeyRegex = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>The variable names the sample asks for, in order of appearance, without duplicates.</summary>
    public static IReadOnlyList<string> ReferencedNames(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => !IsCommentOrBlank(line))
            .SelectMany(line => TokenRegex.Matches(line).Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsCommentOrBlank(string line) => line.Trim().Length == 0 || line.TrimStart().StartsWith('#');

    public static string Render(string template, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);

        // R-68: every variable the sample cannot get is collected over the whole file and reported together, so
        // fixing a deployment never takes one attempt per missing variable.
        var undefined = new List<string>();
        var empty = new List<string>();
        var forbidden = new List<string>();
        var output = new StringBuilder();
        foreach (var rawLine in template.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (IsCommentOrBlank(line))
            {
                output.Append(line).Append('\n');
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            var key = separator > 0 ? line[..separator].Trim() : string.Empty;
            if (!KeyRegex.IsMatch(key))
                throw new BadRequestException(
                    "Environment template line is not KEY=VALUE with a variable name as key.");

            var lineHasProblem = false;
            var value = TokenRegex.Replace(line[(separator + 1)..], match =>
            {
                var name = match.Groups[1].Value;
                if (variables.TryGetValue(name, out var resolved) && resolved.Length > 0
                    && resolved.IndexOfAny(['\r', '\n', '\0']) < 0)
                    return resolved;
                lineHasProblem = true;
                AddOnce(!variables.ContainsKey(name) ? undefined : resolved!.Length == 0 ? empty : forbidden, name);
                return match.Value;
            });
            if (lineHasProblem) continue;
            if (value.Contains("#{", StringComparison.Ordinal))
                throw new BadRequestException(
                    $"Environment template line '{key}' contains an invalid or unresolved token.");

            output.Append(key).Append("=\"")
                .Append(value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal)
                    .Replace("$", "\\$", StringComparison.Ordinal))
                .Append("\"\n");
        }

        if (undefined.Count + empty.Count + forbidden.Count > 0)
            throw new BadRequestException(DescribeMissing(undefined, empty, forbidden));

        // The split leaves one empty element after a final line break; one line break ends the file.
        var rendered = output.ToString().TrimEnd('\n') + "\n";
        if (rendered.Length > MaxRenderedLength)
            throw new BadRequestException(
                $"Rendered environment file exceeds the {MaxRenderedLength} character safety limit.");
        return rendered;
    }

    private static void AddOnce(List<string> names, string name)
    {
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
    }

    private static string Quote(IEnumerable<string> names) => string.Join(", ", names.Select(name => $"'{name}'"));

    /// <summary>One sentence for every variable the sample could not get, each named, with where to define it.</summary>
    private static string DescribeMissing(List<string> undefined, List<string> empty, List<string> forbidden)
    {
        var parts = new List<string>();
        if (undefined.Count > 0)
            parts.Add($"Environment template variable(s) not defined: {Quote(undefined)}. Define each one in a variable "
                + "library or a vault of the project, or in an environment its pipeline reads.");
        if (empty.Count > 0)
            parts.Add($"Environment template variable(s) empty: {Quote(empty)}. Give each a value in the variable "
                + "library or the vault that holds it.");
        if (forbidden.Count > 0)
            parts.Add($"Environment template variable(s) with a forbidden line break or NUL byte: {Quote(forbidden)}.");
        return string.Join(" ", parts);
    }
}
