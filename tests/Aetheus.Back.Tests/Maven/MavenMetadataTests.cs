// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Xml.Linq;
using Aetheus.Back.Components.Shared.Maven;

namespace Aetheus.Back.Tests.Maven;

public sealed class MavenMetadataTests
{
    private static readonly DateTime Updated = new(2026, 10, 17, 12, 30, 45, DateTimeKind.Utc);

    [Fact]
    public void WriteArtifact_ListsVersionsInMavenOrder_WithLatestAndRelease()
    {
        var xml = Encoding.UTF8.GetString(MavenMetadata.WriteArtifact(
            "org.example", "demo", ["1.10", "1.9", "2.0-SNAPSHOT", "1.0", "1.9"], Updated));

        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("org.example", root.Element("groupId")!.Value);
        Assert.Equal("demo", root.Element("artifactId")!.Value);
        var versioning = root.Element("versioning")!;
        Assert.Equal("2.0-SNAPSHOT", versioning.Element("latest")!.Value);
        Assert.Equal("1.10", versioning.Element("release")!.Value);
        Assert.Equal(["1.0", "1.9", "1.10", "2.0-SNAPSHOT"],
            versioning.Element("versions")!.Elements("version").Select(element => element.Value));
        Assert.Equal("20261017123045", versioning.Element("lastUpdated")!.Value);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteArtifact_IsDeterministic_SoItsChecksumsAreStable()
    {
        var first = MavenMetadata.WriteArtifact("g", "a", ["1.0", "1.1"], Updated);
        var second = MavenMetadata.WriteArtifact("g", "a", ["1.1", "1.0"], Updated);
        Assert.Equal(first, second);
        Assert.NotEqual(0xEF, first[0]); // no byte order mark
    }

    [Fact]
    public void WriteArtifact_WithOnlySnapshots_HasNoRelease()
    {
        var root = XDocument.Parse(Encoding.UTF8.GetString(
            MavenMetadata.WriteArtifact("g", "a", ["1.0-SNAPSHOT"], Updated))).Root!;
        Assert.Null(root.Element("versioning")!.Element("release"));
        Assert.Equal("1.0-SNAPSHOT", root.Element("versioning")!.Element("latest")!.Value);
    }

    [Fact]
    public void WriteSnapshot_PointsEachClassifierAndExtensionAtItsNewestBuild()
    {
        var first = new SnapshotBuild("1.0", "20261017.100000", 1);
        var second = new SnapshotBuild("1.0", "20261017.110000", 2);
        var xml = Encoding.UTF8.GetString(MavenMetadata.WriteSnapshot("g", "a", "1.0-SNAPSHOT",
        [
            new SnapshotFile(null, "jar", first, Updated.AddHours(-2)),
            new SnapshotFile(null, "pom", first, Updated.AddHours(-2)),
            new SnapshotFile("sources", "jar", first, Updated.AddHours(-2)),
            new SnapshotFile(null, "jar", second, Updated),
            new SnapshotFile(null, "pom", second, Updated)
        ], Updated));

        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("1.1.0", root.Attribute("modelVersion")!.Value);
        Assert.Equal("1.0-SNAPSHOT", root.Element("version")!.Value);
        var snapshot = root.Element("versioning")!.Element("snapshot")!;
        Assert.Equal("20261017.110000", snapshot.Element("timestamp")!.Value);
        Assert.Equal("2", snapshot.Element("buildNumber")!.Value);
        var entries = root.Descendants("snapshotVersion")
            .Select(entry => $"{entry.Element("classifier")?.Value}|{entry.Element("extension")!.Value}|{entry.Element("value")!.Value}")
            .ToList();
        Assert.Equal(
        [
            "|jar|1.0-20261017.110000-2",
            "|pom|1.0-20261017.110000-2",
            "sources|jar|1.0-20261017.100000-1"
        ], entries);

        Assert.Equal("1.0-20261017.110000-2", MavenMetadata.ReadSnapshotValue(xml, "1.0-SNAPSHOT"));
        Assert.Equal("1.0-20261017.100000-1", MavenMetadata.ReadSnapshotValue(xml, "1.0-SNAPSHOT", "jar", "sources"));
    }

    [Fact]
    public void ReadSnapshotValue_FallsBackToTheLegacyTimestampAndBuildNumber()
    {
        const string xml = """
            <metadata><versioning><snapshot><timestamp>20261017.090000</timestamp><buildNumber>7</buildNumber></snapshot></versioning></metadata>
            """;
        Assert.Equal("2.0-20261017.090000-7", MavenMetadata.ReadSnapshotValue(xml, "2.0-SNAPSHOT"));
    }

    [Fact]
    public void ReadArtifact_RoundTripsAndRefusesExternalEntities()
    {
        var parsed = MavenMetadata.ReadArtifact(Encoding.UTF8.GetString(
            MavenMetadata.WriteArtifact("g", "a", ["1.0", "1.1"], Updated)));
        Assert.Equal(["1.0", "1.1"], parsed.Versions);
        Assert.Equal("1.1", parsed.Release);
        Assert.Equal(Updated, parsed.LastUpdatedUtc);

        const string xxe = """
            <?xml version="1.0"?><!DOCTYPE m [<!ENTITY x SYSTEM "file:///etc/passwd">]><metadata>&x;</metadata>
            """;
        Assert.ThrowsAny<System.Xml.XmlException>(() => MavenMetadata.ReadArtifact(xxe));
    }

    [Theory]
    [InlineData("1.0-SNAPSHOT", true)]
    [InlineData("1.0-20261017.120000-3", true)]
    [InlineData("1.0", false)]
    [InlineData("1.0-20261317.120000-3", false)]
    public void IsSnapshot_RecognisesBothSpellings(string version, bool expected)
        => Assert.Equal(expected, MavenSnapshot.IsSnapshot(version));
}
