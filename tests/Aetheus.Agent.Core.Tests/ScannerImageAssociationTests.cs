// SPDX-License-Identifier: EUPL-1.2
using System.Formats.Tar;
using System.Text;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// A built-image scanner only analyses the image of the run's own commit: the run must name a commit, the
/// archive must resolve, the provenance marker must name that commit, and the image configuration inside the
/// <c>docker save</c> archive must carry it as <c>org.opencontainers.image.revision</c>. Each refusal names
/// what is wrong. The archives are real tar files written into a temporary source tree.
/// </summary>
public sealed class ScannerImageAssociationTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string ConfigPath = "blobs/sha256/cfg";

    private readonly string _source = Directory.CreateTempSubdirectory("aetheus-image-association-").FullName;

    public void Dispose() => Directory.Delete(_source, recursive: true);

    private string ImageDirectory => Path.Combine(_source, ".analysis-image");

    private static readonly ScannerManifestEntry TrivyImage = new() { Key = "trivy-image" };

    private Task<ImageAssociationResult> Validate(string? commit = Commit, ScannerManifestEntry? scanner = null)
    {
        var env = new Dictionary<string, string>();
        if (commit is not null) env["BUILD_SOURCEVERSION"] = commit;
        return ScannerOperationValidator.ValidateImageAssociationAsync(
            scanner ?? TrivyImage, _source, env, TestContext.Current.CancellationToken);
    }

    private void WriteMarker(string commit)
    {
        Directory.CreateDirectory(ImageDirectory);
        File.WriteAllText(Path.Combine(ImageDirectory, "source-commit"), commit + "\n");
    }

    private void WriteArchive(string? manifest, string? configJson)
    {
        Directory.CreateDirectory(ImageDirectory);
        using var stream = File.Create(Path.Combine(ImageDirectory, "app-back.tar"));
        using var writer = new TarWriter(stream);
        if (manifest is not null) Add(writer, "manifest.json", manifest);
        if (configJson is not null) Add(writer, ConfigPath, configJson);
    }

    private static void Add(TarWriter writer, string name, string content) =>
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
        });

    private static string Config(string revision) =>
        """{"config":{"Labels":{"org.opencontainers.image.revision":""" + "\"" + revision + "\"}}}";

    private const string Manifest = $$"""[{"Config":"{{ConfigPath}}","Layers":[]}]""";

    [Fact]
    public async Task AnArchiveBuiltFromTheRunsCommit_IsAccepted()
    {
        WriteMarker(Commit);
        WriteArchive(Manifest, Config(Commit.ToUpperInvariant()));

        Assert.Equal(ImageAssociationResult.Valid, await Validate());
    }

    [Fact]
    public async Task AScannerThatAnalysesNoImage_IsNotChecked()
    {
        Assert.Equal(ImageAssociationResult.Valid, await Validate(commit: null, new ScannerManifestEntry { Key = "semgrep" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("main")]
    public async Task ARunWithoutAUsableCommit_IsRefused(string? commit)
    {
        var result = await Validate(commit);

        Assert.Equal(
            ImageAssociationResult.Refused("the run provides no usable source commit (BUILD_SOURCEVERSION / AETHEUS_SOURCE_VERSION)"),
            result);
    }

    [Fact]
    public async Task AMissingArchive_IsRefusedAsMissing_NotAsAProvenanceMismatch()
    {
        var result = await Validate();

        Assert.False(result.IsValid);
        Assert.Contains("does not exist", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingProvenanceMarker_IsRefused()
    {
        WriteArchive(Manifest, Config(Commit));

        Assert.Equal(
            ImageAssociationResult.Refused("the run's provenance marker .analysis-image/source-commit is missing"),
            await Validate());
    }

    [Fact]
    public async Task ImagesBuiltAtAnotherCommit_AreRefusedNamingBothCommits()
    {
        const string other = "fedcba9876543210fedcba9876543210fedcba98";
        WriteMarker(other);
        WriteArchive(Manifest, Config(other));

        Assert.Equal(
            ImageAssociationResult.Refused($"the images were built at {other}, this run is at {Commit}"),
            await Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""[{"Config":"../escape"}]""")]
    [InlineData("""[{"Config":"a"},{"Config":"b"}]""")]
    [InlineData("""[{"Layers":[]}]""")]
    public async Task AnArchiveWithoutAReadableImageConfiguration_IsRefused(string? manifest)
    {
        WriteMarker(Commit);
        WriteArchive(manifest, Config(Commit));

        Assert.Equal(
            ImageAssociationResult.Refused("'app-back.tar' carries no readable image configuration"),
            await Validate());
    }

    [Theory]
    [InlineData("fedcba9876543210fedcba9876543210fedcba98")]
    [InlineData(null)]
    public async Task AnImageWhoseRevisionLabelIsAnotherCommitOrAbsent_IsRefused(string? revision)
    {
        WriteMarker(Commit);
        WriteArchive(Manifest, revision is null ? """{"config":{"Labels":{}}}""" : Config(revision));

        Assert.Equal(
            ImageAssociationResult.Refused($"'app-back.tar' does not carry org.opencontainers.image.revision {Commit}"),
            await Validate());
    }
}
