// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Inventory;

namespace Aetheus.Back.Components.Analysis.DependencyBump;

/// <summary>
/// PLAN-004, lot 4, point 9: the tag of a base image on the <c>FROM</c> lines of the Dockerfile the match names. Only the lines that
/// name that image at that tag (and, when the request names one, at that digest) change, byte for byte. A digest cannot be derived
/// from a version: a digest-pinned line moves only when the request carries the digest of the target tag
/// (<see cref="DependencyBumpRequest.TargetDigest"/>, read from a registry by the caller), and then it is rewritten as
/// <c>image:tag@digest</c>, never as a tag alone. A build argument is not a line of this file to rewrite and is refused rather than guessed.
/// </summary>
internal static partial class BaseImageBump
{
    private sealed record FromLine(int ImageStart, string Image, int LineNumber);

    private sealed record Pass(List<(FromLine From, string NewTag)> Edits, bool ThroughArgument, bool ThroughDigest);

    /// <summary>The tag the target version gives to an image whose current version is <paramref name="currentVersion"/> (a tag, with
    /// its <c>@digest</c> when pinned): the image's variant (<c>-noble</c>) is kept. The caller resolves the digest of this exact tag.</summary>
    public static string TagFor(string currentVersion, string targetVersion)
    {
        var digest = currentVersion.IndexOf('@', StringComparison.Ordinal);
        return WithVariantOf(digest < 0 ? currentVersion : currentVersion[..digest], targetVersion);
    }

    public static DependencyBumpResult Plan(DependencyBumpRequest request, RepositoryFiles files)
    {
        if (files.Read(request.SourcePath) is not { } text)
            return DependencyBumpResult.Refused(DependencyBumpRefusal.ManifestNotFound, $"'{request.SourcePath}' is not among the files read.");

        var pass = Scan(request, text);
        if (pass.Edits.Count == 0) return Nothing(request, pass);
        var edited = Apply(text, request, pass.Edits);
        var newTag = pass.Edits[0].NewTag;
        var newVersion = request.TargetDigest is null ? newTag : $"{newTag}@{request.TargetDigest}";
        if (!Verifies(request, text, edited, newVersion))
        {
            return DependencyBumpResult.Refused(DependencyBumpRefusal.EditNotVerified,
                $"The edit of '{request.SourcePath}' does not read back as {request.PackageName}:{newVersion} with its other images unchanged.");
        }

        IReadOnlyList<DependencyBumpChange> changes =
            [.. pass.Edits.Select(edit => new DependencyBumpChange($"FROM {request.PackageName}: {request.CurrentVersion} -> {newVersion}", edit.From.LineNumber))];
        return DependencyBumpResult.Done([new DependencyBumpFileEdit(request.SourcePath, edited, changes)], []);
    }

    private static DependencyBumpResult Nothing(DependencyBumpRequest request, Pass pass)
    {
        if (pass.ThroughDigest)
        {
            return DependencyBumpResult.Refused(DependencyBumpRefusal.VersionNotPinned,
                $"'{request.SourcePath}' pins {request.PackageName} by digest, and the digest of the target cannot be derived from its version.");
        }

        return pass.ThroughArgument
            ? DependencyBumpResult.Refused(DependencyBumpRefusal.VersionNotPinned,
                $"'{request.SourcePath}' builds a base image from a build argument: a bump does not rewrite an argument it cannot tie to this image.")
            : DependencyBumpResult.Refused(DependencyBumpRefusal.NotDeclared,
                $"No FROM line of '{request.SourcePath}' names {request.PackageName}:{request.CurrentVersion}.");
    }

    private static Pass Scan(DependencyBumpRequest request, string text)
    {
        var edits = new List<(FromLine From, string NewTag)>();
        var throughArgument = false;
        var throughDigest = false;
        var (currentTag, currentDigest) = SplitVersion(request.CurrentVersion);
        foreach (var from in FromLines(text))
        {
            throughArgument |= from.Image.Contains('$', StringComparison.Ordinal);
            var (name, tag, digest) = Split(from.Image);
            if (!string.Equals(name, request.PackageName, StringComparison.Ordinal) || !string.Equals(tag, currentTag, StringComparison.Ordinal)) continue;
            if (!string.Equals(digest, currentDigest, StringComparison.Ordinal))
            {
                // The same image and tag, pinned (or not) differently from what the match names: not this match's line.
                throughDigest |= currentDigest is null && digest is not null;
                continue;
            }

            edits.Add((from, WithVariantOf(currentTag, request.TargetVersion)));
        }

        return new Pass(edits, throughArgument, throughDigest);
    }

    private static IEnumerable<FromLine> FromLines(string text)
    {
        var start = 0;
        for (var lineNumber = 1; start < text.Length; lineNumber++)
        {
            var end = text.IndexOf('\n', start);
            var stop = end < 0 ? text.Length : end;
            if (FromPattern().Match(text, start, stop - start) is { Success: true } match)
                yield return new FromLine(match.Groups[1].Index, match.Groups[1].Value, lineNumber);
            start = stop + 1;
        }
    }

    /// <summary>The tag, and the digest when the version is <c>tag@digest</c>, as the inventory spells an image version.</summary>
    private static (string Tag, string? Digest) SplitVersion(string version)
    {
        var at = version.IndexOf('@', StringComparison.Ordinal);
        return at < 0 ? (version, null) : (version[..at], version[(at + 1)..]);
    }

    /// <summary>A colon before the last slash is a registry port, not a tag. The tag is null when there is none; the digest is null when not pinned.</summary>
    private static (string Name, string? Tag, string? Digest) Split(string image)
    {
        var at = image.IndexOf('@', StringComparison.Ordinal);
        var reference = at < 0 ? image : image[..at];
        var digest = at < 0 ? null : image[(at + 1)..];
        var colon = reference.LastIndexOf(':');
        return colon > reference.LastIndexOf('/')
            ? (reference[..colon], reference[(colon + 1)..], digest)
            : (reference, null, digest);
    }

    /// <summary>The target names a version; what follows the version in the current tag is the image's variant (<c>-alpine3.19</c>). A prerelease label is not one.</summary>
    private static string WithVariantOf(string current, string target)
    {
        var dash = current.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || target.Contains('-', StringComparison.Ordinal) || PrereleaseLabel().IsMatch(current[(dash + 1)..])) return target;
        return target + current[dash..];
    }

    private static string Apply(string text, DependencyBumpRequest request, List<(FromLine From, string NewTag)> edits)
    {
        var result = new StringBuilder(text.Length + edits.Count);
        var cursor = 0;
        foreach (var (from, newTag) in edits)
        {
            result.Append(text, cursor, from.ImageStart - cursor).Append(request.PackageName).Append(':').Append(newTag);
            if (request.TargetDigest is not null) result.Append('@').Append(request.TargetDigest);
            cursor = from.ImageStart + from.Image.Length;
        }

        return result.Append(text, cursor, text.Length - cursor).ToString();
    }

    private static bool Verifies(DependencyBumpRequest request, string original, string edited, string newVersion)
    {
        var expected = DockerfileParser.Parse(original).Entries.Select(entry =>
            entry.Name == request.PackageName && entry.Version == request.CurrentVersion ? (entry.Name, Version: newVersion) : (entry.Name, entry.Version));
        return expected.SequenceEqual(DockerfileParser.Parse(edited).Entries.Select(entry => (entry.Name, entry.Version)));
    }

    [GeneratedRegex(@"\G[ \t]*FROM[ \t]+(?:--\S+[ \t]+)*([^\s#]\S*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FromPattern();

    [GeneratedRegex(@"^(alpha|beta|rc|preview|pre)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrereleaseLabel();
}
