// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Finds the files of the source repository a copied definition refers to (an adapter script, a compose
/// file, an Apache template, a Dockerfile). The copy cannot carry them: they live in the source
/// project's repository, not in the pipeline. They are detected, never solved, so the user knows what
/// the new project's repository must provide before the copy can run.
/// <para>
/// A reference counts when it is a relative path with at least one directory that either ends in a
/// well-known file extension (or is a Dockerfile or Makefile), or starts under <c>.pipeline/</c>,
/// <c>deploy/</c> or <c>scripts/</c>. Absolute paths, URLs and bare file names are not repository
/// references and are ignored, as are comment lines.
/// </para>
/// </summary>
internal static partial class PipelineCopyRepositoryFiles
{
    private const string Extensions =
        "sh|bash|ps1|mjs|cjs|js|ts|yml|yaml|json|toml|conf|cfg|ini|env|tpl|template|sql|xml|csproj|sln|slnx|props|targets|md|txt";

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_.\-/:@~$])(?<path>"
        + @"(?:\.\.?/)?(?:[A-Za-z0-9_.\-]+/)+(?:Dockerfile|Containerfile|Makefile)(?:\.[A-Za-z0-9_\-]+)?"
        + @"|(?:\.\.?/)?(?:[A-Za-z0-9_.\-]+/)+[A-Za-z0-9_\-][A-Za-z0-9_.\-]*\.(?:" + Extensions + @")"
        + @"|(?:\.pipeline|deploy|scripts)/[A-Za-z0-9_./\-]*[A-Za-z0-9_]/?"
        + @")(?![A-Za-z0-9_/\-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPath { get; }

    /// <summary>The checkout is the repository: <c>$(WORKSPACE)/deploy/x.sh</c> is <c>deploy/x.sh</c>.</summary>
    [GeneratedRegex(@"\$(?:\(WORKSPACE\)|\{WORKSPACE\}|WORKSPACE)/", RegexOptions.CultureInvariant)]
    private static partial Regex WorkspacePrefix { get; }

    /// <summary>The distinct repository paths the definition names, in order of appearance.</summary>
    internal static List<string> Find(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var content = string.Join('\n', yaml.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));
        content = WorkspacePrefix.Replace(content, string.Empty);
        return [.. RepositoryPath.Matches(content)
            .Select(match => match.Groups["path"].Value)
            .Select(path => path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path)
            .Distinct(StringComparer.Ordinal)];
    }
}
