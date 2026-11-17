// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Components.VulnerabilityWatch.VersionComparison;

namespace Aetheus.Back.Components.Analysis.DependencyBump;

/// <summary>
/// PLAN-004, lot 4, points 6 to 8: the change that moves one dependency to the version a recommendation names.
/// It is a pure function of the files it is given: it reads nothing, writes nothing and runs nothing. It returns the new text of the
/// manifests that declare the version, and names, with the command that refreshes them, the lock files that only a package manager
/// can write. A bump that cannot be made says why and returns no edit: it is never a success that changed nothing, and a
/// lock file is never edited by hand into a state the package manager would not have produced.
/// </summary>
public static partial class DependencyBumper
{
    private const string NuGet = "NuGet";
    private const string NpmEcosystem = "npm";
    private const string Packagist = "Packagist";
    private const string Docker = "docker";
    private const string Runtime = "runtime";

    // The inventory stores npm names in lower case (VulnerabilityPackageName.Normalize) while package.json writes them as published
    // (JSONStream), so the names are compared without case and the manifest's own spelling is the one written back.
    private static readonly JsonBumpProfile Npm = new(
        NpmEcosystem, VersionSpec.Dialect.Npm, "package.json", ["dependencies", "devDependencies", "optionalDependencies"], StringComparison.OrdinalIgnoreCase,
        ["package-lock.json", "npm-shrinkwrap.json"], ["pnpm-lock.yaml", "yarn.lock"], "node_modules",
        _ => "npm install --package-lock-only --ignore-scripts");

    private static readonly JsonBumpProfile Composer = new(
        Packagist, VersionSpec.Dialect.Composer, "composer.json", ["require", "require-dev"], StringComparison.OrdinalIgnoreCase,
        ["composer.lock"], [], "vendor",
        request => $"composer update {request.PackageName} --no-install --no-scripts --no-plugins --no-interaction");

    /// <summary>Plans the bump of <paramref name="request"/> over <paramref name="files"/>: the manifests of the repository by path with forward slashes,
    /// and the lock files (their text is only read to see whether they name the package, so an empty text is enough).</summary>
    public static DependencyBumpResult Plan(DependencyBumpRequest request, IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(files);
        var invalid = Validate(request, out var normalized);
        if (invalid is not null) return DependencyBumpResult.Refused(DependencyBumpRefusal.InvalidRequest, invalid);
        if (RepositoryFiles.Create(files) is not { } repository)
            return DependencyBumpResult.Refused(DependencyBumpRefusal.InvalidRequest, "A file path is empty or leaves the repository.");

        request = normalized!;
        if (request.Ecosystem is not (NuGet or NpmEcosystem or Packagist or Docker or Runtime))
            return DependencyBumpResult.Refused(DependencyBumpRefusal.UnsupportedEcosystem, $"No bump is written for {request.Ecosystem}.");
        if (DigestRefusal(request) is { } digestRefusal) return digestRefusal;
        var order = VersionComparators.Compare(request.Ecosystem, request.CurrentVersion, request.TargetVersion);
        if (order != VersionOrder.Less)
        {
            return DependencyBumpResult.Refused(DependencyBumpRefusal.TargetNotAbove,
                order == VersionOrder.Unknown
                    ? $"{request.CurrentVersion} and {request.TargetVersion} cannot be ordered, so a bump cannot tell that it moves forward."
                    : $"{request.TargetVersion} is not above {request.CurrentVersion}: a bump only moves a package forward.");
        }

        return request.Ecosystem switch
        {
            NuGet => NuGetBump.Plan(request, repository),
            NpmEcosystem => JsonManifestBump.Plan(Npm, request, repository),
            Docker => BaseImageBump.Plan(request, repository),
            Runtime => RuntimeBump.Plan(request, repository),
            _ => JsonManifestBump.Plan(Composer, request, repository)
        };
    }

    /// <summary>An image pinned by digest needs the digest of its target; a line that pins no digest never gains one.</summary>
    private static DependencyBumpResult? DigestRefusal(DependencyBumpRequest request)
    {
        var pinnedByDigest = request.Ecosystem == Docker && request.CurrentVersion.Contains('@', StringComparison.Ordinal);
        if (pinnedByDigest && request.TargetDigest is null)
            return DependencyBumpResult.Refused(DependencyBumpRefusal.VersionNotPinned,
                $"{request.PackageName} is pinned by digest, and the digest of the target cannot be derived from its version.");
        if (!pinnedByDigest && request.TargetDigest is not null)
            return DependencyBumpResult.Refused(DependencyBumpRefusal.InvalidRequest,
                $"A digest was given for {request.PackageName}, which the request does not name by digest: a digest is never added to a line that did not pin one.");
        return null;
    }

    /// <summary>The request refused for a field that is empty or not what its ecosystem allows, or null with the request cleaned of path spelling.
    /// The names and the target end up in a command line and in a file: they are checked to the characters their ecosystem allows.</summary>
    private static string? Validate(DependencyBumpRequest request, out DependencyBumpRequest? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(request.Ecosystem) || string.IsNullOrWhiteSpace(request.PackageName)
            || string.IsNullOrWhiteSpace(request.CurrentVersion) || string.IsNullOrWhiteSpace(request.TargetVersion))
            return "The ecosystem, the package and both versions are required.";
        if (RepositoryFiles.Normalize(request.SourcePath) is not { } source)
            return "The source path is empty or leaves the repository.";
        var name = request.PackageName;
        if (NameShape(request.Ecosystem) is { } nameShape && !nameShape.IsMatch(name)) return $"'{name}' is not a {request.Ecosystem} package name.";
        var target = request.TargetVersion;
        if (!TargetShape().IsMatch(target)) return $"'{target}' is not a version a bump can write.";
        if (request.TargetDigest is { } digest && (request.Ecosystem != Docker || !Sha256Digest().IsMatch(digest)))
            return $"'{digest}' is not a sha256 image digest, or the ecosystem is not an image.";
        normalized = new DependencyBumpRequest(request.Ecosystem, name, request.CurrentVersion, target, source, request.TargetDigest);
        return null;
    }

    /// <summary>The characters a package name of <paramref name="ecosystem"/> may hold, or null for an ecosystem a bump does not write.</summary>
    private static Regex? NameShape(string ecosystem) => ecosystem switch
    {
        NuGet => NuGetName(),
        NpmEcosystem => NpmName(),
        Packagist => PackagistName(),
        Docker => ImageName(),
        Runtime => RuntimeName(),
        _ => null
    };

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Digest();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NuGetName();

    // Old packages such as JSONStream are written with capitals; npm compares names as written.
    [GeneratedRegex(@"^(@[A-Za-z0-9][A-Za-z0-9._~\-]*/)?[A-Za-z0-9][A-Za-z0-9._~\-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NpmName();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.\-]*/[a-z0-9][a-z0-9_.\-]*\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PackagistName();

    // A registry host with its optional port, then the path of the image; a tag is not part of the name.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]*(:[0-9]{1,5})?(/[a-z0-9][a-z0-9._\-]*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ImageName();

    // As the inventory names a runtime.
    [GeneratedRegex(@"^(dotnet|nodejs|php|go)\z", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeName();

    [GeneratedRegex(@"^v?\d+(\.\d+){0,3}(-[0-9A-Za-z.\-]+)?(\+[0-9A-Za-z.\-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex TargetShape();
}
