// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VulnerabilityWatch.VersionComparison;

namespace Aetheus.Back.Components.Analysis.DependencyBump;

/// <summary>What differs between npm and Composer for a bump: the names of their files, sections and lock files.</summary>
/// <param name="Sections">The sections a project's own requirements live in. Peer requirements and overrides are not the project's choice of version, so they are not here.</param>
/// <param name="LockNames">The lock files this bump drives with <paramref name="RefreshCommand"/>.</param>
/// <param name="OtherLockNames">Lock files of another package manager for the same manifest: a bump does not drive them.</param>
/// <param name="InstalledFolder">The folder a package manager fills with other packages' manifests, which a bump never reads.</param>
internal sealed record JsonBumpProfile(
    string Ecosystem,
    VersionSpec.Dialect Dialect,
    string ManifestName,
    IReadOnlyList<string> Sections,
    StringComparison NameComparison,
    IReadOnlyList<string> LockNames,
    IReadOnlyList<string> OtherLockNames,
    string InstalledFolder,
    Func<DependencyBumpRequest, string> RefreshCommand);

/// <summary>
/// The bump of a package declared in a JSON manifest with a lock file beside it (npm, Composer). The manifest holds a
/// requirement, not the locked version: the requirement is raised to the target, keeping its operator, and the lock file,
/// which only the package manager can write consistently (resolved versions, integrity hashes, a content hash of the manifest),
/// is named with the command that refreshes it and never edited.
/// </summary>
internal static class JsonManifestBump
{
    public static DependencyBumpResult Plan(JsonBumpProfile profile, DependencyBumpRequest request, RepositoryFiles files)
    {
        var sourceName = RepositoryFiles.NameOf(request.SourcePath);
        var directory = RepositoryFiles.DirectoryOf(request.SourcePath);
        if (profile.OtherLockNames.Contains(sourceName, StringComparer.OrdinalIgnoreCase))
            return DependencyBumpResult.Refused(DependencyBumpRefusal.UnsupportedLockFile,
                $"'{request.SourcePath}' belongs to a package manager this bump does not drive: run its own command to move {request.PackageName} to {request.TargetVersion}.");
        var sourceIsLock = profile.LockNames.Contains(sourceName, StringComparer.OrdinalIgnoreCase);
        if (!sourceIsLock && !sourceName.Equals(profile.ManifestName, StringComparison.OrdinalIgnoreCase))
            return DependencyBumpResult.Refused(DependencyBumpRefusal.ManifestNotFound,
                $"The {profile.Ecosystem} bump reads {profile.ManifestName} and {string.Join(", ", profile.LockNames)}, not '{request.SourcePath}'.");
        if (profile.OtherLockNames.Any(name => files.Find(directory, name) is not null))
            return DependencyBumpResult.Refused(DependencyBumpRefusal.UnsupportedLockFile,
                $"'{directory}' holds the lock file of another package manager besides {string.Join(" or ", profile.LockNames)}: which one is the project's is not for a bump to guess.");

        var lockFile = sourceIsLock ? request.SourcePath : profile.LockNames.Select(name => files.Find(directory, name)).FirstOrDefault(found => found is not null);
        var manifests = ManifestsOf(profile, request, files, directory, lockFile);
        if (manifests.Count == 0)
            return DependencyBumpResult.Refused(DependencyBumpRefusal.ManifestNotFound, $"No {profile.ManifestName} was given for '{directory}'.");

        var pass = new JsonPass();
        foreach (var manifest in manifests)
        {
            if (Edit(profile, request, manifest, files.Read(manifest)!, pass) is { } failure) return failure;
        }

        if (pass.Edits.Count == 0) return NothingToBump(profile, request, directory, pass);

        IReadOnlyList<DependencyBumpLockRefresh> locks =
            lockFile is null ? [] : [new DependencyBumpLockRefresh(lockFile, directory, profile.RefreshCommand(request with { PackageName = pass.SpelledName ?? request.PackageName }))];
        return DependencyBumpResult.Done(pass.Edits, locks);
    }

    /// <summary>The manifests a bump edits: without a lock file the manifest is the whole project; with one, every manifest of the workspace it locks.</summary>
    private static List<string> ManifestsOf(JsonBumpProfile profile, DependencyBumpRequest request, RepositoryFiles files, string directory, string? lockFile) =>
        lockFile is null
            ? (files.Contains(request.SourcePath) ? [request.SourcePath] : [])
            : [.. files.Under(string.Empty)
                .Where(path => RepositoryFiles.NameOf(path).Equals(profile.ManifestName, StringComparison.Ordinal))
                .Where(path => !path.Split('/').SkipLast(1).Contains(profile.InstalledFolder, StringComparer.Ordinal))
                .Where(path => OwnerOf(profile, files, directory, RepositoryFiles.DirectoryOf(path)) == directory)];

    /// <summary>The refusal of a pass that edited nothing: a requirement a bump does not rewrite, or none that holds the current version.</summary>
    private static DependencyBumpResult NothingToBump(JsonBumpProfile profile, DependencyBumpRequest request, string directory, JsonPass pass) =>
        pass.Skipped.Count > 0
            ? DependencyBumpResult.Refused(DependencyBumpRefusal.VersionNotPinned, string.Join(" ", pass.Skipped))
            : DependencyBumpResult.Refused(DependencyBumpRefusal.NotDeclared,
                $"No {profile.ManifestName} of '{directory}' requires {request.PackageName} in a range that holds {request.CurrentVersion}: it comes in through another package, whose own requirement has to be raised"
                + (pass.Elsewhere.Count > 0 ? $" ({string.Join("; ", pass.Elsewhere)})." : "."));

    /// <summary>The directory of the lock file a manifest belongs to: its own, or the nearest one above (npm workspaces share the root's).</summary>
    private static string? OwnerOf(JsonBumpProfile profile, RepositoryFiles files, string lockDirectory, string manifestDirectory)
    {
        foreach (var ancestor in RepositoryFiles.AncestorsOrSelf(manifestDirectory))
        {
            // The directory of the lock file being bumped holds it even when the caller left it out of the files.
            if (ancestor == lockDirectory || profile.LockNames.Concat(profile.OtherLockNames).Any(name => files.Find(ancestor, name) is not null))
                return ancestor;
        }

        return null;
    }

    private static DependencyBumpResult? Edit(JsonBumpProfile profile, DependencyBumpRequest request, string path, string text, JsonPass pass)
    {
        var read = JsonManifest.Read(text, profile.Sections);
        if (read is null || read.RepeatedSection)
            return DependencyBumpResult.Refused(DependencyBumpRefusal.UnreadableManifest,
                $"'{path}' is not a JSON object with each of its sections written once.");
        var named = read.Declarations.Where(declaration => string.Equals(declaration.Name, request.PackageName, profile.NameComparison)).ToList();
        if (named.GroupBy(declaration => declaration.Section).Any(group => group.Count() > 1))
            return DependencyBumpResult.Refused(DependencyBumpRefusal.UnreadableManifest, $"'{path}' requires {request.PackageName} twice in one section.");

        var edits = new List<(JsonDeclaration Declaration, string NewValue)>();
        foreach (var declaration in named)
        {
            var spec = declaration.Escaped || declaration.Value is null ? null : VersionSpec.Parse(declaration.Value);
            if (spec is null)
            {
                pass.Skipped.Add($"'{path}' requires {request.PackageName} as {Describe(declaration)}, which is not a plain version requirement (^, ~, >= or an exact version): a bump does not rewrite a range, a tag, an alias or an address.");
                continue;
            }

            if (PackageVersionOrdering.IsPreRelease(profile.Ecosystem, request.CurrentVersion) || !spec.Admits(profile.Ecosystem, profile.Dialect, request.CurrentVersion))
            {
                pass.Elsewhere.Add($"'{path}' requires it as '{declaration.Value}'");
                continue;
            }

            // The spec admits the current version, which is below the target: its floor is below the target too, so this always changes the text.
            edits.Add((declaration, spec.With(request.TargetVersion)));
        }

        if (edits.Count == 0) return null;
        pass.SpelledName ??= SpelledAs(edits[0].Declaration.Name, request.PackageName);
        var changed = JsonManifest.Replace(text, edits, out var error);
        if (changed is null)
            return DependencyBumpResult.Refused(DependencyBumpRefusal.EditNotVerified, $"The edit of '{path}' was refused: {error}");
        pass.Edits.Add(new DependencyBumpFileEdit(path, changed,
            [.. edits.Select(edit => new DependencyBumpChange($"{edit.Declaration.Section}: {edit.Declaration.Name} {edit.Declaration.Value} -> {edit.NewValue}", edit.Declaration.Line))]));
        return null;
    }

    /// <summary>The name as the manifest spells it (the inventory's lower case may differ from it). The spelling ends up in the refresh
    /// command, and a manifest is repository content, so a spelling that is not the requested name apart from its case is not used.</summary>
    private static string SpelledAs(string declared, string requested) =>
        declared.Length == requested.Length && declared.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '~' or '@' or '/')
        && string.Equals(declared, requested, StringComparison.OrdinalIgnoreCase)
            ? declared
            : requested;

    private static string Describe(JsonDeclaration declaration) => declaration.Value is null ? "a value that is not a string" : $"'{declaration.Value}'";

    private sealed class JsonPass
    {
        public List<DependencyBumpFileEdit> Edits { get; } = [];
        public List<string> Skipped { get; } = [];
        public List<string> Elsewhere { get; } = [];
        public string? SpelledName { get; set; }
    }
}
