// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Shared.Maven;

namespace Aetheus.Back.Components.PackageRegistry;

public enum MavenPathKind
{
    /// <summary><c>{group}/{artifact}/maven-metadata.xml</c>.</summary>
    ArtifactMetadata,

    /// <summary><c>{group}/{artifact}/{x-SNAPSHOT}/maven-metadata.xml</c>.</summary>
    SnapshotMetadata,

    /// <summary><c>{group}/{artifact}/{version}/{artifact}-{version}[-{classifier}].{extension}</c>.</summary>
    File
}

public enum MavenChecksum
{
    None,
    Md5,
    Sha1,
    Sha256,
    Sha512
}

/// <summary>
/// A request path inside the Maven repository, parsed and validated. Every segment is checked against
/// a strict character set that cannot spell <c>.</c>, <c>..</c>, a separator or an encoded byte, so a
/// path that reaches the storage is always one of the three layouts above.
/// </summary>
public sealed partial record MavenRepositoryPath(
    MavenPathKind Kind,
    string GroupId,
    string ArtifactId,
    string? Version,
    string FileName,
    MavenChecksum Checksum,
    string? Classifier = null,
    string? Extension = null,
    SnapshotBuild? Build = null)
{
    public const int MaxPathLength = 1024;
    public const int MaxNameLength = 300;
    public const int MaxVersionLength = 100;

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_\-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupSegment();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactSegment();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-+]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionSegment();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-+]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileSegment();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ClassifierPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{0,15}(\.[A-Za-z][A-Za-z0-9]{0,15}){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPattern();

    [GeneratedRegex(@"^(?<timestamp>\d{8}\.\d{6})-(?<build>[1-9]\d{0,8})(?<rest>[-.].*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampedRest();

    /// <summary><c>groupId:artifactId</c>, the package name the registry stores.</summary>
    public string Coordinates => $"{GroupId}:{ArtifactId}";

    /// <summary>The group as repository directories (<c>org.example</c> becomes <c>org/example</c>).</summary>
    public string GroupPath => GroupId.Replace('.', '/');

    public static bool TryParse(string? path, out MavenRepositoryPath? parsed, out string error)
    {
        parsed = null;
        error = "Invalid Maven repository path.";
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength)
            return false;
        var segments = path.TrimStart('/').Split('/');
        if (segments.Length < 3 || segments.Any(segment => segment.Length == 0))
            return false;

        var last = segments[^1];
        var checksum = StripChecksum(ref last);
        if (!FileSegment().IsMatch(last))
            return false;

        if (string.Equals(last, MavenMetadata.FileName, StringComparison.Ordinal))
            return TryParseMetadata(segments, checksum, out parsed);
        return TryParseFile(segments, last, checksum, out parsed, ref error);
    }

    private static bool TryParseMetadata(string[] segments, MavenChecksum checksum, out MavenRepositoryPath? parsed)
    {
        parsed = null;
        var parent = segments[^2];
        if (segments.Length >= 4 && parent.EndsWith(MavenSnapshot.Suffix, StringComparison.Ordinal))
        {
            if (!TryCoordinates(segments[..^3], segments[^3], out var groupId)
                || !VersionSegment().IsMatch(parent))
                return false;
            parsed = new MavenRepositoryPath(MavenPathKind.SnapshotMetadata, groupId, segments[^3], parent,
                MavenMetadata.FileName, checksum);
            return true;
        }

        if (!TryCoordinates(segments[..^2], parent, out var artifactGroup))
            return false;
        parsed = new MavenRepositoryPath(MavenPathKind.ArtifactMetadata, artifactGroup, parent, null,
            MavenMetadata.FileName, checksum);
        return true;
    }

    private static bool TryParseFile(
        string[] segments, string fileName, MavenChecksum checksum, out MavenRepositoryPath? parsed, ref string error)
    {
        parsed = null;
        if (segments.Length < 4)
            return false;
        var version = segments[^2];
        var artifactId = segments[^3];
        if (!TryCoordinates(segments[..^3], artifactId, out var groupId) || !VersionSegment().IsMatch(version))
            return false;

        var prefix = artifactId + "-";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
        {
            error = $"File name must start with '{prefix}'.";
            return false;
        }

        var remainder = fileName[prefix.Length..];
        SnapshotBuild? build = null;
        string rest;
        if (version.EndsWith(MavenSnapshot.Suffix, StringComparison.Ordinal))
        {
            var baseVersion = version[..^MavenSnapshot.Suffix.Length];
            var match = remainder.StartsWith(baseVersion + "-", StringComparison.Ordinal)
                ? TimestampedRest().Match(remainder[(baseVersion.Length + 1)..])
                : Match.Empty;
            if (!match.Success)
            {
                error = "A snapshot file must carry a unique timestamped version (yyyyMMdd.HHmmss-N).";
                return false;
            }

            build = new SnapshotBuild(baseVersion, match.Groups["timestamp"].Value,
                int.Parse(match.Groups["build"].Value, CultureInfo.InvariantCulture));
            if (!MavenSnapshot.TryParseTimestamped(build.Value.Value, out _))
                return false;
            rest = match.Groups["rest"].Value;
        }
        else
        {
            if (!remainder.StartsWith(version, StringComparison.Ordinal))
            {
                error = $"File name must carry the version '{version}'.";
                return false;
            }
            rest = remainder[version.Length..];
        }

        string? classifier = null;
        string extension;
        if (rest.StartsWith('-'))
        {
            var dot = rest.IndexOf('.');
            if (dot < 0)
                return false;
            classifier = rest[1..dot];
            extension = rest[(dot + 1)..];
            if (!ClassifierPattern().IsMatch(classifier))
                return false;
        }
        else if (rest.StartsWith('.'))
        {
            extension = rest[1..];
        }
        else
        {
            return false;
        }

        if (!ExtensionPattern().IsMatch(extension))
            return false;
        parsed = new MavenRepositoryPath(MavenPathKind.File, groupId, artifactId, version, fileName, checksum,
            classifier, extension, build);
        return true;
    }

    private static bool TryCoordinates(string[] groupSegments, string artifactId, out string groupId)
    {
        groupId = string.Join('.', groupSegments);
        return groupSegments.Length > 0
               && groupSegments.All(segment => GroupSegment().IsMatch(segment))
               && ArtifactSegment().IsMatch(artifactId)
               && groupId.Length + artifactId.Length + 1 <= MaxNameLength;
    }

    private static MavenChecksum StripChecksum(ref string fileName)
    {
        foreach (var (suffix, kind) in Suffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.Ordinal))
            {
                fileName = fileName[..^suffix.Length];
                return kind;
            }
        }
        return MavenChecksum.None;
    }

    private static readonly (string Suffix, MavenChecksum Kind)[] Suffixes =
    [
        (".sha512", MavenChecksum.Sha512),
        (".sha256", MavenChecksum.Sha256),
        (".sha1", MavenChecksum.Sha1),
        (".md5", MavenChecksum.Md5)
    ];
}
