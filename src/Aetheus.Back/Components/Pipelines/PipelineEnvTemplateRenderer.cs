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

            var value = TokenRegex.Replace(line[(separator + 1)..], match =>
            {
                var name = match.Groups[1].Value;
                if (!variables.TryGetValue(name, out var resolved))
                    throw new BadRequestException(
                        $"Environment template variable '{name}' is not defined.");
                if (resolved.Length == 0)
                    throw new BadRequestException(
                        $"Environment template variable '{name}' is empty: give it a value in its library or vault.");
                if (resolved.IndexOfAny(['\r', '\n', '\0']) >= 0)
                    throw new BadRequestException(
                        $"Environment template variable '{name}' contains a forbidden line break or NUL byte.");
                return resolved;
            });
            if (value.Contains("#{", StringComparison.Ordinal))
                throw new BadRequestException(
                    $"Environment template line '{key}' contains an invalid or unresolved token.");

            output.Append(key).Append("=\"")
                .Append(value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal)
                    .Replace("$", "\\$", StringComparison.Ordinal))
                .Append("\"\n");
        }

        // The split leaves one empty element after a final line break; one line break ends the file.
        var rendered = output.ToString().TrimEnd('\n') + "\n";
        if (rendered.Length > MaxRenderedLength)
            throw new BadRequestException(
                $"Rendered environment file exceeds the {MaxRenderedLength} character safety limit.");
        return rendered;
    }
}
