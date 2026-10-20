// SPDX-License-Identifier: EUPL-1.2
using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>A scanner image is accepted only when its archive manifest names exactly one image config whose
/// OCI revision label equals the revision the task asked for; any ambiguity or missing label is a refusal.</summary>
public sealed class ScannerImageArchiveTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("scanner-image-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Archive(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tar");
        using (var stream = File.Create(path))
        using (var writer = new TarWriter(stream))
        {
            foreach (var (name, content) in entries)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
                });
            }
        }
        return path;
    }

    [Fact]
    public async Task FindImageConfigPathAsync_ASingleImageManifest_ReturnsItsConfigPath()
    {
        var path = Archive(("layer.tar", "x"), ("manifest.json", """[{"Config":"blobs/abc.json","Layers":[]}]"""));

        Assert.Equal("blobs/abc.json", await ScannerOperationValidator.FindImageConfigPathAsync(path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""[{"Config":"a.json"},{"Config":"b.json"}]""")]
    [InlineData("[]")]
    [InlineData("""{"Config":"a.json"}""")]
    [InlineData("""[{"Layers":[]}]""")]
    [InlineData("""[{"Config":42}]""")]
    public async Task FindImageConfigPathAsync_AnAmbiguousOrIncompleteManifest_ReturnsNull(string manifest)
    {
        var path = Archive(("manifest.json", manifest));

        Assert.Null(await ScannerOperationValidator.FindImageConfigPathAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindImageConfigPathAsync_AnArchiveWithoutManifest_ReturnsNull()
    {
        var path = Archive(("other.json", """[{"Config":"a.json"}]"""));

        Assert.Null(await ScannerOperationValidator.FindImageConfigPathAsync(path, TestContext.Current.CancellationToken));
    }

    private static string ConfigWithRevision(string? revision) => revision is null
        ? """{"config":{"Labels":{}}}"""
        : "{\"config\":{\"Labels\":{\"org.opencontainers.image.revision\":\"" + revision + "\"}}}";

    [Theory]
    [InlineData("0123456789abcdef", "0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF", "0123456789abcdef", true)]
    [InlineData("fedcba9876543210", "0123456789abcdef", false)]
    public async Task ImageConfigMatchesRevisionAsync_ComparesTheRevisionLabelIgnoringCase(
        string label, string expected, bool matches)
    {
        var path = Archive(("blobs/abc.json", ConfigWithRevision(label)));

        Assert.Equal(matches, await ScannerOperationValidator.ImageConfigMatchesRevisionAsync(
            path, "blobs/abc.json", expected, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImageConfigMatchesRevisionAsync_AConfigWithoutTheLabel_DoesNotMatch()
    {
        var path = Archive(("blobs/abc.json", ConfigWithRevision(null)));

        Assert.False(await ScannerOperationValidator.ImageConfigMatchesRevisionAsync(
            path, "blobs/abc.json", "0123456789abcdef", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImageConfigMatchesRevisionAsync_TheNamedConfigIsNotInTheArchive_DoesNotMatch()
    {
        var path = Archive(("blobs/other.json", ConfigWithRevision("0123456789abcdef")));

        Assert.False(await ScannerOperationValidator.ImageConfigMatchesRevisionAsync(
            path, "blobs/abc.json", "0123456789abcdef", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{"a":{"b":"value"}}""", true, "value")]
    [InlineData("""{"a":{"b":"  "}}""", false, "  ")]
    [InlineData("""{"a":{"b":5}}""", false, null)]
    [InlineData("""{"a":"text"}""", false, null)]
    [InlineData("""{"x":{"b":"value"}}""", false, null)]
    public void TryGetNestedString_WalksTheObjectPathAndRequiresANonBlankString(string json, bool found, string? value)
    {
        using var document = JsonDocument.Parse(json);

        var result = ScannerOperationValidator.TryGetNestedString(document.RootElement, out var actual, "a", "b");

        Assert.Equal(found, result);
        Assert.Equal(value, actual);
    }
}
