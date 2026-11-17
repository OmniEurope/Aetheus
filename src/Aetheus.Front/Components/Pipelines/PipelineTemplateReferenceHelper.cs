// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Pipelines;

internal sealed record PipelineTemplateReference(string Name, int? Version);

internal static partial class PipelineTemplateReferenceHelper
{
    [GeneratedRegex("(?im)^(?<prefix>\\s*extends\\s*:\\s*)(?<quote>['\\\"]?)(?<reference>[^'\\\"#\\r\\n]+)\\k<quote>(?<suffix>\\s*(?:#.*)?)$")]
    private static partial Regex ExtendsLinePattern();

    /// <summary>R-13: a pipeline says its application has a database by <c>APPLICATION_HAS_DATABASE: "true"</c> (what the setup
    /// wizard writes) or, for a blue-green deployment written by hand, by the migrations directory the migration gate reads
    /// (<c>BG_MIGRATIONS_DIR</c>, not empty).</summary>
    [GeneratedRegex("(?im)^\\s*(?:APPLICATION_HAS_DATABASE\\s*:\\s*['\\\"]?true['\\\"]?|BG_MIGRATIONS_DIR\\s*:\\s*['\\\"]?[^'\\\"\\s#])")]
    private static partial Regex DatabasePattern();

    /// <summary>Whether the pipeline's variables declare a database (<see cref="DatabasePattern"/>).</summary>
    public static bool DeclaresDatabase(string yaml) => DatabasePattern().IsMatch(yaml);

    public static PipelineTemplateReference? Parse(string yaml)
    {
        var match = ExtendsLinePattern().Match(yaml);
        return match.Success ? ParseReference(match.Groups["reference"].Value) : null;
    }

    /// <summary>Reads the value of an <c>extends:</c> line (<c>name@version</c>, or the legacy bare
    /// <c>name</c>, which means the latest version); null for a blank value.</summary>
    public static PipelineTemplateReference? ParseReference(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return null;
        var separator = value.LastIndexOf('@');
        if (separator > 0 && int.TryParse(value[(separator + 1)..], out var version))
            return new PipelineTemplateReference(value[..separator], version);
        return new PipelineTemplateReference(value, null);
    }

    public static string Pin(string yaml, string name, int version) =>
        ExtendsLinePattern().Replace(yaml, match =>
        {
            var suffix = match.Groups["suffix"].Value;
            if (suffix.StartsWith('#')) suffix = $" {suffix}";
            return $"{match.Groups["prefix"].Value}'{name.Replace("'", "''", StringComparison.Ordinal)}@{version}'{suffix}";
        }, 1);
}
