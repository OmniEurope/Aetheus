// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// What the distribution is made of, port of <c>planExport</c> and <c>unlistedTopLevel</c> of
/// <c>scripts/export-public-distribution.mjs</c>: for every source file, where it goes, or that it
/// stays behind. The overlay comes last and wins: an overlay file replaces the source file of the same
/// path. A rename onto a path the source already has is a <see cref="Refusal"/>, where the script threw.
/// </summary>
/// <param name="Copies">Distribution path to the source path it is read from.</param>
/// <param name="Excluded">The source files the boundary leaves out.</param>
/// <param name="Refusal">Why the export is refused, or null.</param>
internal sealed record PublicDistributionPlan(
    IReadOnlyDictionary<string, string> Copies, IReadOnlyList<string> Excluded, string? Refusal)
{
    /// <summary>The folder of the files written for the public repository alone.</summary>
    public const string Overlay = "deploy/public-distribution/overlay/";

    public static PublicDistributionPlan Create(IReadOnlyList<string> sourceFiles, PublicDistributionBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentNullException.ThrowIfNull(boundary);
        var globs = new PublicDistributionGlobs(boundary.Exclude);
        var renames = boundary.Rename.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var copies = new Dictionary<string, string>(StringComparer.Ordinal);
        var excluded = new List<string>();
        foreach (var file in sourceFiles.Where(file => !file.StartsWith(Overlay, StringComparison.Ordinal)))
        {
            if (globs.IsMatch(file))
                excluded.Add(file);
            else
                copies[renames.GetValueOrDefault(file) ?? file] = file;
        }
        foreach (var file in sourceFiles.Where(file => file.StartsWith(Overlay, StringComparison.Ordinal)))
            copies[file[Overlay.Length..]] = file;

        var present = sourceFiles.ToHashSet(StringComparer.Ordinal);
        var clash = boundary.Rename.Select(entry => entry.Value).FirstOrDefault(present.Contains);
        return new PublicDistributionPlan(
            copies, excluded, clash is null ? null : $"rename target '{clash}' already exists in the source");
    }

    /// <summary>Rule 2 of the manifest, mechanised: the top-level entries nobody listed, sorted.</summary>
    public static IReadOnlyList<string> UnlistedTopLevel(IEnumerable<string> paths, IReadOnlyList<string> topLevel)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(topLevel);
        var listed = topLevel.ToHashSet(StringComparer.Ordinal);
        return [.. paths
            .Select(path => path.Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .Where(entry => !listed.Contains(entry))
            .Order(StringComparer.Ordinal)];
    }
}
