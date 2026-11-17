// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Splits a pipeline YAML definition into lines, flagging each <c>pipeline: &lt;name&gt;</c>
/// reference so the read-only YAML viewers (run YAML tab + edit-page peek) can render those as clickable
/// links that open the referenced pipeline's definition. Shared so both viewers parse identically. A viewer
/// that can open a template also asks for the <c>extends: &lt;template&gt;@&lt;version&gt;</c> references
/// (R-35); the text after such a reference (closing quote, comment) is kept in <see cref="Line.Suffix"/>.</summary>
internal static partial class PipelineYamlLines
{
    public sealed record Line(string Text, string? PipelineRef, string? TemplateRef = null, string Suffix = "");

    [GeneratedRegex(@"^(?<prefix>\s*(?:-\s*)?pipeline:\s*)(?<name>\S+)\s*$")]
    private static partial Regex RefRegex();

    [GeneratedRegex(@"^(?<prefix>\s*extends:\s*['""]?)(?<reference>[^'""#\s]+)(?<suffix>['""]?\s*(?:#.*)?)$")]
    private static partial Regex ExtendsRegex();

    public static List<Line> Parse(string? yaml, bool templateReferences = false)
    {
        if (string.IsNullOrEmpty(yaml)) return [];
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var result = new List<Line>(lines.Length);
        foreach (var line in lines)
        {
            var m = RefRegex().Match(line);
            if (m.Success)
            {
                result.Add(new Line(m.Groups["prefix"].Value, m.Groups["name"].Value.Trim('"', '\'')));
                continue;
            }
            var extends = templateReferences ? ExtendsRegex().Match(line) : Match.Empty;
            result.Add(extends.Success
                ? new Line(extends.Groups["prefix"].Value, null, extends.Groups["reference"].Value, extends.Groups["suffix"].Value)
                : new Line(line, null));
        }
        return result;
    }
}
