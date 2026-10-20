// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Aetheus.Back.Components.Shared.Maven;

/// <summary>
/// The two <c>maven-metadata.xml</c> documents of a Maven 2 repository: the artifact-level one
/// (<c>{group}/{artifact}/maven-metadata.xml</c>, the list of versions with <c>latest</c> and
/// <c>release</c>) and the snapshot one (<c>{group}/{artifact}/{x-SNAPSHOT}/maven-metadata.xml</c>, the
/// latest build and the file each classifier/extension resolves to). Writing is deterministic, so the
/// same state always yields the same bytes and the same checksums.
/// </summary>
public static class MavenMetadata
{
    public const string FileName = "maven-metadata.xml";
    private const string TimestampFormat = "yyyyMMddHHmmss";

    public static string FormatLastUpdated(DateTime utc)
        => utc.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>Artifact-level metadata. Versions are written in Maven order; <c>latest</c> is the
    /// highest of them and <c>release</c> the highest non-snapshot.</summary>
    public static byte[] WriteArtifact(
        string groupId, string artifactId, IEnumerable<string> versions, DateTime lastUpdatedUtc)
    {
        var ordered = versions
            .Distinct(StringComparer.Ordinal)
            .OrderBy(version => version, MavenComparableVersion.Ordering)
            .ToList();
        var release = ordered.LastOrDefault(version => !MavenSnapshot.IsSnapshot(version));
        var versioning = new XElement("versioning");
        if (ordered.Count > 0)
            versioning.Add(new XElement("latest", ordered[^1]));
        if (release is not null)
            versioning.Add(new XElement("release", release));
        versioning.Add(
            new XElement("versions", ordered.Select(version => new XElement("version", version))),
            new XElement("lastUpdated", FormatLastUpdated(lastUpdatedUtc)));

        return Serialize(new XElement("metadata",
            new XElement("groupId", groupId),
            new XElement("artifactId", artifactId),
            versioning));
    }

    /// <summary>Snapshot metadata for one <c>-SNAPSHOT</c> version, built from its deployed files.
    /// The latest build is the highest build number; each classifier/extension pair points at its
    /// newest file.</summary>
    public static byte[] WriteSnapshot(
        string groupId, string artifactId, string version, IEnumerable<SnapshotFile> files, DateTime lastUpdatedUtc)
    {
        var list = files.ToList();
        var lastUpdated = FormatLastUpdated(lastUpdatedUtc);
        var versioning = new XElement("versioning");
        var builds = list.Where(file => file.Build is not null).Select(file => file.Build!.Value).ToList();
        if (builds.Count > 0)
        {
            var latest = builds.MaxBy(build => build.BuildNumber);
            versioning.Add(new XElement("snapshot",
                new XElement("timestamp", latest.Timestamp),
                new XElement("buildNumber", latest.BuildNumber.ToString(CultureInfo.InvariantCulture))));
        }

        versioning.Add(new XElement("lastUpdated", lastUpdated));
        var entries = list
            .GroupBy(file => (file.Classifier ?? string.Empty, file.Extension))
            .Select(group => group
                .OrderBy(file => file.Build?.BuildNumber ?? 0)
                .ThenBy(file => file.UpdatedUtc)
                .Last())
            .OrderBy(file => file.Classifier ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(file => file.Extension, StringComparer.Ordinal)
            .Select(file => new XElement("snapshotVersion",
                string.IsNullOrEmpty(file.Classifier) ? null : new XElement("classifier", file.Classifier),
                new XElement("extension", file.Extension),
                new XElement("value", file.Build?.Value ?? version),
                new XElement("updated", FormatLastUpdated(file.UpdatedUtc))))
            .ToList();
        if (entries.Count > 0)
            versioning.Add(new XElement("snapshotVersions", entries));

        return Serialize(new XElement("metadata",
            new XAttribute("modelVersion", "1.1.0"),
            new XElement("groupId", groupId),
            new XElement("artifactId", artifactId),
            new XElement("version", version),
            versioning));
    }

    /// <summary>Reads an artifact-level document (as served by an upstream repository).</summary>
    public static ArtifactMetadata ReadArtifact(string xml)
    {
        var root = SafeXml.Load(xml).Root
                   ?? throw new FormatException("maven-metadata.xml has no root element.");
        var versioning = root.Element("versioning");
        return new ArtifactMetadata(
            root.Element("groupId")?.Value,
            root.Element("artifactId")?.Value,
            versioning?.Element("versions")?.Elements("version").Select(element => element.Value.Trim())
                .Where(value => value.Length > 0).ToList() ?? [],
            NullIfEmpty(versioning?.Element("latest")?.Value),
            NullIfEmpty(versioning?.Element("release")?.Value),
            ParseLastUpdated(versioning?.Element("lastUpdated")?.Value));
    }

    /// <summary>Reads the timestamped value a snapshot document resolves for one classifier/extension,
    /// falling back to the legacy <c>timestamp</c>/<c>buildNumber</c> pair.</summary>
    public static string? ReadSnapshotValue(string xml, string version, string extension = "jar", string? classifier = null)
    {
        var root = SafeXml.Load(xml).Root
                   ?? throw new FormatException("maven-metadata.xml has no root element.");
        var versioning = root.Element("versioning");
        var entry = versioning?.Element("snapshotVersions")?.Elements("snapshotVersion")
            .FirstOrDefault(element =>
                string.Equals(element.Element("extension")?.Value, extension, StringComparison.Ordinal)
                && string.Equals(NullIfEmpty(element.Element("classifier")?.Value), classifier, StringComparison.Ordinal));
        if (NullIfEmpty(entry?.Element("value")?.Value) is { } value)
            return value;

        var snapshot = versioning?.Element("snapshot");
        var timestamp = NullIfEmpty(snapshot?.Element("timestamp")?.Value);
        var build = NullIfEmpty(snapshot?.Element("buildNumber")?.Value);
        if (timestamp is null || build is null || !version.EndsWith(MavenSnapshot.Suffix, StringComparison.Ordinal))
            return null;
        return $"{version[..^MavenSnapshot.Suffix.Length]}-{timestamp}-{build}";
    }

    private static DateTime? ParseLastUpdated(string? value)
        => DateTime.TryParseExact(value?.Trim(), TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static byte[] Serialize(XElement root)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
            new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(writer);
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }
}

/// <summary>What an artifact-level <c>maven-metadata.xml</c> declares.</summary>
public sealed record ArtifactMetadata(
    string? GroupId,
    string? ArtifactId,
    IReadOnlyList<string> Versions,
    string? Latest,
    string? Release,
    DateTime? LastUpdatedUtc);

/// <summary>One deployed file of a snapshot version, as the snapshot metadata lists it.</summary>
public sealed record SnapshotFile(string? Classifier, string Extension, SnapshotBuild? Build, DateTime UpdatedUtc);
