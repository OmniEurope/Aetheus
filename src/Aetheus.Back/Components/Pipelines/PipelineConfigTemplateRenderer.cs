// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Pipelines;

internal static class PipelineConfigTemplateRenderer
{
    private const int MaxRenderedLength = 256 * 1024;
    private static readonly Regex TokenRegex = new(
        @"#\{([A-Za-z_][A-Za-z0-9_.-]*)\}#",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static bool IsSafeConfigPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\\', '/').Trim();
        return normalized.StartsWith(".pipeline/configs/", StringComparison.Ordinal)
            && normalized.Length <= 240
            && !normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or "..");
    }

    /// <summary>The <c>#{NAME}#</c> tokens <see cref="RenderStrict"/> will have to resolve, so a launch can
    /// refuse a template it could never render before the run executes anything (R-32).</summary>
    public static IEnumerable<string> FindTokenNames(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        foreach (Match match in TokenRegex.Matches(template))
            yield return match.Groups[1].Value;
    }

    public static string RenderStrict(
        string template,
        IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);

        var rendered = TokenRegex.Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            if (!variables.TryGetValue(name, out var value))
                throw new BadRequestException(
                    $"Configuration template variable '{name}' is not defined.");
            if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new BadRequestException(
                    $"Configuration template variable '{name}' contains a forbidden line break or NUL byte.");
            return value;
        });

        if (rendered.Contains("#{", StringComparison.Ordinal))
            throw new BadRequestException("Configuration template contains an invalid or unresolved token.");
        if (rendered.Length > MaxRenderedLength)
            throw new BadRequestException(
                $"Rendered configuration exceeds the {MaxRenderedLength} character safety limit.");
        return rendered;
    }
}
