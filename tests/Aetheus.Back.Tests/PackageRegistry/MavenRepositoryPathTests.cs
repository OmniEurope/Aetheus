// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PackageRegistry;

namespace Aetheus.Back.Tests.PackageRegistry;

public sealed class MavenRepositoryPathTests
{
    [Fact]
    public void ReleaseFile_IsSplitIntoCoordinatesClassifierAndExtension()
    {
        Assert.True(MavenRepositoryPath.TryParse(
            "org/example/tools/demo-lib/1.2.0/demo-lib-1.2.0-sources.jar", out var path, out _));
        Assert.Equal(MavenPathKind.File, path!.Kind);
        Assert.Equal("org.example.tools", path.GroupId);
        Assert.Equal("demo-lib", path.ArtifactId);
        Assert.Equal("1.2.0", path.Version);
        Assert.Equal("sources", path.Classifier);
        Assert.Equal("jar", path.Extension);
        Assert.Equal(MavenChecksum.None, path.Checksum);
        Assert.Equal("org.example.tools:demo-lib", path.Coordinates);
        Assert.Null(path.Build);
    }

    [Theory]
    [InlineData("g/a/1.0/a-1.0.jar.sha1", MavenChecksum.Sha1, "a-1.0.jar", "jar")]
    [InlineData("g/a/1.0/a-1.0.pom.md5", MavenChecksum.Md5, "a-1.0.pom", "pom")]
    [InlineData("g/a/1.0/a-1.0.tar.gz.sha256", MavenChecksum.Sha256, "a-1.0.tar.gz", "tar.gz")]
    [InlineData("g/a/1.0/a-1.0.jar.asc.sha512", MavenChecksum.Sha512, "a-1.0.jar.asc", "jar.asc")]
    public void ChecksumSuffix_NamesTheFileItDescribes(string raw, MavenChecksum checksum, string fileName, string extension)
    {
        Assert.True(MavenRepositoryPath.TryParse(raw, out var path, out _));
        Assert.Equal(checksum, path!.Checksum);
        Assert.Equal(fileName, path.FileName);
        Assert.Equal(extension, path.Extension);
    }

    [Fact]
    public void SnapshotFile_CarriesItsTimestampedBuild()
    {
        Assert.True(MavenRepositoryPath.TryParse(
            "com/acme/app/2.0-SNAPSHOT/app-2.0-20261017.120000-14-jar-with-dependencies.jar", out var path, out _));
        Assert.Equal("2.0-SNAPSHOT", path!.Version);
        Assert.Equal("jar-with-dependencies", path.Classifier);
        Assert.Equal("2.0-20261017.120000-14", path.Build!.Value.Value);
        Assert.Equal(14, path.Build.Value.BuildNumber);
    }

    [Fact]
    public void Metadata_IsArtifactLevelUnlessUnderASnapshotVersion()
    {
        Assert.True(MavenRepositoryPath.TryParse("org/example/demo/maven-metadata.xml", out var artifact, out _));
        Assert.Equal(MavenPathKind.ArtifactMetadata, artifact!.Kind);
        Assert.Equal("org.example", artifact.GroupId);
        Assert.Equal("demo", artifact.ArtifactId);

        Assert.True(MavenRepositoryPath.TryParse("org/example/demo/1.0-SNAPSHOT/maven-metadata.xml.sha1", out var snapshot, out _));
        Assert.Equal(MavenPathKind.SnapshotMetadata, snapshot!.Kind);
        Assert.Equal("1.0-SNAPSHOT", snapshot.Version);
        Assert.Equal(MavenChecksum.Sha1, snapshot.Checksum);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("g/../a/1.0/a-1.0.jar")]
    [InlineData("g/a/../a-1.0.jar")]
    [InlineData("g/a/1.0/..")]
    [InlineData("g/./a/1.0/a-1.0.jar")]
    [InlineData("g/a/1.0/.a-1.0.jar")]
    [InlineData("g\\a/1.0/a-1.0.jar")]
    [InlineData("g/a/1.0/a-1.0.jar/")]
    [InlineData("g//a/1.0/a-1.0.jar")]
    [InlineData("g/a/1.0/a-1.0%2Fjar")]
    [InlineData("g/a/1.0/a-1.0.j\u0000ar")]
    [InlineData("C:/a/1.0/a-1.0.jar")]
    public void TraversalAndForeignCharacters_AreRejected(string raw)
        => Assert.False(MavenRepositoryPath.TryParse(raw, out _, out _));

    [Theory]
    [InlineData("")]
    [InlineData("a-1.0.jar")]
    [InlineData("a/1.0/a-1.0.jar")]
    [InlineData("g/a/1.0/b-1.0.jar")]
    [InlineData("g/a/1.0/a-1.1.jar")]
    [InlineData("g/a/1.0/a-1.0.1.jar")]
    [InlineData("g/a/1.0/a-1.0")]
    [InlineData("g/a/1.0/a-1.0-.jar")]
    [InlineData("g/a/1.0-SNAPSHOT/a-1.0-SNAPSHOT.jar")]
    [InlineData("g/a/1.0-SNAPSHOT/a-1.0-20261317.120000-1.jar")]
    [InlineData("g.x/a/1.0/a-1.0.jar")]
    public void MalformedLayouts_AreRejected(string raw)
        => Assert.False(MavenRepositoryPath.TryParse(raw, out _, out _));

    [Fact]
    public void OverlongPath_IsRejected()
        => Assert.False(MavenRepositoryPath.TryParse(
            string.Join('/', Enumerable.Repeat("g", 600)) + "/a/1.0/a-1.0.jar", out _, out _));
}
