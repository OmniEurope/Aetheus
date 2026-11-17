// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// R-30: which .NET channel a repository without <c>global.json</c> needs. Reads the
/// <c>TargetFramework</c> / <c>TargetFrameworks</c> of <c>Directory.Build.props</c> and of the project files
/// (<c>.csproj</c>, <c>.fsproj</c>, <c>.vbproj</c>) within <see cref="MaxDepth"/> levels of a root, and keeps the
/// highest modern moniker (<c>net10.0</c> gives channel <c>10.0</c>), the SDK that builds all of them.
/// <c>netstandard</c>, <c>netcoreapp</c> and .NET Framework monikers (<c>net48</c>) name no channel and are ignored.
/// </summary>
internal static partial class DotnetTargetFrameworkProbe
{
    /// <summary>Directory levels below the root that are searched, as the templates' own look.</summary>
    internal const int MaxDepth = 4;

    private const int MaxFilesRead = 200;

    private static readonly HashSet<string> SkippedDirectories =
        new(["node_modules", ".git", "bin", "obj", ".pipeline-artifacts"], StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    [GeneratedRegex(@"<TargetFrameworks?>([^<]*)</TargetFrameworks?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetFrameworkElement { get; }

    [GeneratedRegex(@"(?<![A-Za-z0-9])net(\d+)\.(\d+)(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex ModernMoniker { get; }

    /// <summary>The highest modern target framework of the repository: its moniker (<c>net10.0</c>), its channel
    /// (<c>10.0</c>) and the file that declares it relative to the root.</summary>
    internal sealed record Finding(string Moniker, string Channel, string File);

    /// <summary>The finding, or null when no file declares a literal modern target framework.</summary>
    public static Finding? Find(string root)
    {
        if (!Directory.Exists(root))
            return null;
        Finding? best = null;
        var bestVersion = (Major: -1, Minor: -1);
        var read = 0;
        foreach (var file in CandidateFiles(root))
        {
            if (read++ >= MaxFilesRead)
                break;
            foreach (var version in MonikersIn(File.ReadAllText(file)))
            {
                if (version.CompareTo(bestVersion) <= 0)
                    continue;
                bestVersion = version;
                best = new Finding(
                    $"net{version.Major}.{version.Minor}",
                    $"{version.Major}.{version.Minor}",
                    Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }
        return best;
    }

    private static IEnumerable<(int Major, int Minor)> MonikersIn(string text) =>
        TargetFrameworkElement.Matches(text)
            .SelectMany(element => ModernMoniker.Matches(element.Groups[1].Value))
            .Select(moniker => (
                int.Parse(moniker.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(moniker.Groups[2].Value, CultureInfo.InvariantCulture)));

    /// <summary>The candidate files breadth first, in name order within a directory.</summary>
    private static IEnumerable<string> CandidateFiles(string root)
    {
        var level = new List<string> { root };
        for (var depth = 0; depth <= MaxDepth && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var directory in level)
            {
                foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).Where(IsCandidate))
                    yield return file;
                next.AddRange(Directory.EnumerateDirectories(directory)
                    .Where(child => !SkippedDirectories.Contains(Path.GetFileName(child)))
                    .Order(StringComparer.Ordinal));
            }
            level = next;
        }
    }

    private static bool IsCandidate(string file) =>
        string.Equals(Path.GetFileName(file), "Directory.Build.props", StringComparison.Ordinal)
        || ProjectExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);
}
