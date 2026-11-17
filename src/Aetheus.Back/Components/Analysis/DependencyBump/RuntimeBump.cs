// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Analysis.DependencyBump;

/// <summary>
/// PLAN-004, lot 4, point 9: the runtime version a repository states, in the file the inventory read it from: <c>global.json</c>,
/// <c>.nvmrc</c>, the <c>engines</c> of <c>package.json</c>, the <c>php</c> requirement of <c>composer.json</c>, the <c>go</c> line of
/// <c>go.mod</c>. The file must state exactly the version the match names: a constraint is not a version to move, it is refused
/// earlier because it cannot be ordered against the target.
/// </summary>
internal static partial class RuntimeBump
{
    private const string ComposerLock = "composer.lock";
    private const string ComposerRefresh = "composer update --lock --no-install --no-scripts --no-plugins --no-interaction";

    public static DependencyBumpResult Plan(DependencyBumpRequest request, RepositoryFiles files)
    {
        if (files.Read(request.SourcePath) is not { } text)
            return DependencyBumpResult.Refused(DependencyBumpRefusal.ManifestNotFound, $"'{request.SourcePath}' is not among the files read.");

        return (request.PackageName, RepositoryFiles.NameOf(request.SourcePath).ToLowerInvariant()) switch
        {
            ("dotnet", "global.json") => InJson(request, text, "sdk", "version", files, false),
            ("nodejs", ".nvmrc") => InNvmrc(request, text),
            ("nodejs", "package.json") => InJson(request, text, "engines", "node", files, false),
            ("php", "composer.json") => InJson(request, text, "require", "php", files, true),
            ("go", "go.mod") => InGoMod(request, text),
            _ => NotStated(request)
        };
    }

    private static DependencyBumpResult NotStated(DependencyBumpRequest request) =>
        DependencyBumpResult.Refused(DependencyBumpRefusal.NotDeclared, $"'{request.SourcePath}' does not state {request.PackageName} {request.CurrentVersion}.");

    private static DependencyBumpResult InJson(DependencyBumpRequest request, string text, string section, string name, RepositoryFiles files, bool composer)
    {
        var read = JsonManifest.Read(text, [section]);
        if (read is null || read.RepeatedSection)
            return DependencyBumpResult.Refused(DependencyBumpRefusal.UnreadableManifest, $"'{request.SourcePath}' is not a JSON object with each of its sections written once.");
        var declaration = read.Declarations.SingleOrDefault(candidate => candidate.Name == name);
        if (declaration is null || declaration.Value != request.CurrentVersion) return NotStated(request);

        var changed = JsonManifest.Replace(text, [(declaration, request.TargetVersion)], out var error);
        if (changed is null)
            return DependencyBumpResult.Refused(DependencyBumpRefusal.EditNotVerified, $"The edit of '{request.SourcePath}' was refused: {error}");
        var edit = new DependencyBumpFileEdit(request.SourcePath, changed,
            [new DependencyBumpChange($"{section}.{name}: {request.CurrentVersion} -> {request.TargetVersion}", declaration.Line)]);
        return DependencyBumpResult.Done([edit], composer ? ComposerLockOf(request, files) : []);
    }

    /// <summary>The content hash of <c>composer.lock</c> covers the requirements: changing one leaves it behind.</summary>
    private static IReadOnlyList<DependencyBumpLockRefresh> ComposerLockOf(DependencyBumpRequest request, RepositoryFiles files)
    {
        var directory = RepositoryFiles.DirectoryOf(request.SourcePath);
        return files.Find(directory, ComposerLock) is { } lockFile ? [new DependencyBumpLockRefresh(lockFile, directory, ComposerRefresh)] : [];
    }

    private static DependencyBumpResult InNvmrc(DependencyBumpRequest request, string text)
    {
        var match = NvmrcPattern().Match(text);
        if (!match.Success || match.Groups[2].Value != request.CurrentVersion.TrimStart('v')) return NotStated(request);
        var edited = match.Groups[1].Value + request.TargetVersion.TrimStart('v') + match.Groups[3].Value;
        return DependencyBumpResult.Done(
            [new DependencyBumpFileEdit(request.SourcePath, edited, [new DependencyBumpChange($"node: {request.CurrentVersion} -> {request.TargetVersion}", 1)])], []);
    }

    private static DependencyBumpResult InGoMod(DependencyBumpRequest request, string text)
    {
        var lines = GoLine().Matches(text).Where(match => match.Groups[1].Value == request.CurrentVersion).ToList();
        if (lines.Count != 1) return NotStated(request);
        var version = lines[0].Groups[1];
        var edited = string.Concat(text.AsSpan(0, version.Index), request.TargetVersion, text.AsSpan(version.Index + version.Length));
        var line = text.AsSpan(0, version.Index).Count('\n') + 1;
        return DependencyBumpResult.Done(
            [new DependencyBumpFileEdit(request.SourcePath, edited, [new DependencyBumpChange($"go: {request.CurrentVersion} -> {request.TargetVersion}", line)])], []);
    }

    /// <summary>One version on one line, with its optional <c>v</c> and its line ending kept apart.</summary>
    [GeneratedRegex(@"\A(v?)([0-9][0-9A-Za-z.\-]*)(\r?\n?)\z", RegexOptions.CultureInvariant)]
    private static partial Regex NvmrcPattern();

    [GeneratedRegex(@"^go[ \t]+(\S+)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex GoLine();
}
