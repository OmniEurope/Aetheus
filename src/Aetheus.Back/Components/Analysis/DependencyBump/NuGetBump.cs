// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Components.VulnerabilityWatch.VersionComparison;

namespace Aetheus.Back.Components.Analysis.DependencyBump;

/// <summary>
/// The bump of a NuGet package. The version is written either in the project file (<c>Version</c> on a
/// <c>PackageReference</c>) or, with central package management, in the nearest <c>Directory.Packages.props</c>
/// (<c>PackageVersion</c>); every declaration of the package at exactly the current version is changed, and no other. A version
/// that is a property, a floating or a range is not touched. <c>packages.lock.json</c> holds resolved versions and content hashes
/// that only <c>dotnet restore</c> writes, so it is named with that command and never edited.
/// </summary>
internal static partial class NuGetBump
{
    private const string Ecosystem = "NuGet";
    private const string LockName = "packages.lock.json";
    private const string RestoreCommand = "dotnet restore --force-evaluate";

    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    public static DependencyBumpResult Plan(DependencyBumpRequest request, RepositoryFiles files)
    {
        var sourceName = RepositoryFiles.NameOf(request.SourcePath);
        var directory = RepositoryFiles.DirectoryOf(request.SourcePath);
        var sourceIsLock = sourceName.Equals(LockName, StringComparison.OrdinalIgnoreCase);
        if (!sourceIsLock && !IsProject(sourceName) && !IsMsBuildImport(sourceName))
            return DependencyBumpResult.Refused(DependencyBumpRefusal.ManifestNotFound,
                $"The NuGet bump reads {LockName}, project files and MSBuild .props or .targets files, not '{request.SourcePath}'.");

        var candidates = Candidates(request, files, sourceIsLock, directory);
        if (candidates.Count == 0)
            return DependencyBumpResult.Refused(DependencyBumpRefusal.ManifestNotFound, $"No project file or MSBuild file was given for '{directory}'.");

        var skipped = new List<string>();
        var elsewhere = new List<string>();
        var edits = new List<DependencyBumpFileEdit>();
        foreach (var path in candidates)
        {
            var versions = MsBuildDocument.ReadVersions(files.Read(path)!, request.PackageName);
            if (versions is null)
                return DependencyBumpResult.Refused(DependencyBumpRefusal.UnreadableManifest, $"'{path}' is not an XML document.");
            var toChange = new List<MsBuildVersion>();
            foreach (var version in versions) Classify(request, path, version, toChange, skipped, elsewhere);
            if (toChange.Count == 0) continue;
            var changed = MsBuildDocument.Replace(files.Read(path)!, toChange, request.TargetVersion, out var error);
            if (changed is null)
                return DependencyBumpResult.Refused(DependencyBumpRefusal.EditNotVerified, $"The edit of '{path}' was refused: {error}");
            edits.Add(new DependencyBumpFileEdit(path, changed,
                [.. toChange.Select(version => new DependencyBumpChange($"{version.ElementName} {request.PackageName} {request.CurrentVersion} -> {request.TargetVersion}", version.Line))]));
        }

        if (edits.Count == 0)
        {
            return skipped.Count > 0
                ? DependencyBumpResult.Refused(DependencyBumpRefusal.VersionNotPinned, string.Join(" ", skipped))
                : DependencyBumpResult.Refused(DependencyBumpRefusal.NotDeclared,
                    $"No project or MSBuild file of '{directory}' declares {request.PackageName} at {request.CurrentVersion}: it comes in through another package"
                    + (elsewhere.Count > 0 ? $" ({string.Join("; ", elsewhere)})." : "."));
        }

        return DependencyBumpResult.Done(edits, LocksToRefresh(request, files, edits, sourceIsLock));
    }

    private static List<string> Candidates(DependencyBumpRequest request, RepositoryFiles files, bool sourceIsLock, string directory)
    {
        var paths = new List<string>();
        if (sourceIsLock) paths.AddRange(files.InDirectory(directory).Where(path => IsProject(RepositoryFiles.NameOf(path))));
        else if (files.Contains(request.SourcePath)) paths.Add(request.SourcePath);
        if (files.NearestUp(directory, "Directory.Packages.props") is { } central) paths.Add(central);
        foreach (var ancestor in RepositoryFiles.AncestorsOrSelf(directory))
        {
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
                if (files.Find(ancestor, name) is { } found) paths.Add(found);
        }

        return [.. paths.Distinct(StringComparer.Ordinal)];
    }

    private static void Classify(
        DependencyBumpRequest request, string path, MsBuildVersion version, List<MsBuildVersion> toChange, List<string> skipped, List<string> elsewhere)
    {
        if (version.Value is null)
        {
            skipped.Add($"'{path}' line {version.Line} writes the {version.Holder} of {request.PackageName} in a form a bump does not edit in place (an entity, a CDATA section or markup).");
        }
        else if (!Pinned().IsMatch(version.Value))
        {
            skipped.Add($"'{path}' line {version.Line} gives {request.PackageName} the version '{version.Value}', which is a property, a floating version or a range, not one version: a bump does not choose what it stands for.");
        }
        else if (VersionComparators.Compare(Ecosystem, version.Value, request.CurrentVersion) == VersionOrder.Equal)
        {
            toChange.Add(version);
        }
        else
        {
            elsewhere.Add($"'{path}' line {version.Line} declares {version.Value}");
        }
    }

    /// <summary>The lock files that describe the packages of what was edited: next to an edited project, or anywhere below an edited shared file.</summary>
    private static List<DependencyBumpLockRefresh> LocksToRefresh(
        DependencyBumpRequest request, RepositoryFiles files, List<DependencyBumpFileEdit> edits, bool sourceIsLock)
    {
        var locks = new SortedDictionary<string, DependencyBumpLockRefresh>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            var editedDirectory = RepositoryFiles.DirectoryOf(edit.Path);
            var scope = IsProject(RepositoryFiles.NameOf(edit.Path)) ? files.InDirectory(editedDirectory) : files.Under(editedDirectory);
            foreach (var lockPath in scope.Where(path => RepositoryFiles.NameOf(path).Equals(LockName, StringComparison.OrdinalIgnoreCase)))
            {
                var content = files.Read(lockPath)!;
                // A lock file given with its content that never names the package does not change; one given empty is only present.
                if (content.Length > 0 && !content.Contains(request.PackageName, StringComparison.OrdinalIgnoreCase)) continue;
                locks[lockPath] = new DependencyBumpLockRefresh(lockPath, RepositoryFiles.DirectoryOf(lockPath), RestoreCommand);
            }
        }

        if (sourceIsLock)
            locks.TryAdd(request.SourcePath, new DependencyBumpLockRefresh(request.SourcePath, RepositoryFiles.DirectoryOf(request.SourcePath), RestoreCommand));
        return [.. locks.Values];
    }

    private static bool IsProject(string name) => ProjectExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsMsBuildImport(string name) =>
        name.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\d+(\.\d+){0,3}(-[0-9A-Za-z.\-]+)?(\+[0-9A-Za-z.\-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pinned();
}
