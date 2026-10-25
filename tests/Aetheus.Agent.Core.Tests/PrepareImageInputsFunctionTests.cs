// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the former <c>prepare-image-inputs.sh</c> script, run through the real executor
/// on real gzip archives in a temp workspace: the revision, delivery-contract and digest checks with
/// the script's messages, and the unpacked <c>.analysis-image/</c> the image scanners read.
/// </summary>
public sealed class PrepareImageInputsFunctionTests : IDisposable
{
    private const string Source = "0123456789abcdef0123456789abcdef01234567";
    private const string Other = "fedcba9876543210fedcba9876543210fedcba98";
    private const string Input = "ws/.security-image-input/.pipeline-artifacts";

    private readonly FunctionTestHarness _harness = new("aetheus-image-inputs");
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal) { ["BUILD_SOURCEVERSION"] = Source };
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal) { ["images"] = "app-back.tar.gz app-front.tar.gz" };
    private readonly Dictionary<string, object> _contract;

    public PrepareImageInputsFunctionTests()
    {
        _harness.Write($"{Input}/source-commit", Source + "\n");
        _contract = new Dictionary<string, object>
        {
            ["schema"] = 2,
            ["sourceSha"] = Source,
            ["artifacts"] = new Dictionary<string, string>
            {
                ["app-back.tar.gz"] = WriteArchive("app-back.tar.gz", "back image"),
                ["app-front.tar.gz"] = WriteArchive("app-front.tar.gz", "front image")
            }
        };
    }

    public void Dispose() => _harness.Dispose();

    private string InputPath(string name) => Path.Combine(_harness.Root, Input, name);

    private string ImagePath(string name) => Path.Combine(_harness.Workspace, ".analysis-image", name);

    private Dictionary<string, string> Digests => (Dictionary<string, string>)_contract["artifacts"];

    /// <summary>Writes a gzip archive of <paramref name="content"/> and returns its SHA-256.</summary>
    private string WriteArchive(string name, string content)
    {
        var path = InputPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            gzip.Write(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        File.WriteAllText(InputPath("delivery-contract.json"), JsonSerializer.Serialize(_contract));
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new PrepareImageInputsFunction(), _env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, _) = await RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        return result.FailureReason!;
    }

    [Fact]
    public async Task VerifiedArchives_AreUnpackedWithTheRevisionMarker()
    {
        _harness.Write("ws/.analysis-image/stale.tar", "old");
        _harness.Write($"{Input}/source-commit", Source + "\r\n");

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(variables);
        Assert.Equal("back image", await File.ReadAllTextAsync(ImagePath("app-back.tar"), TestContext.Current.CancellationToken));
        Assert.Equal("front image", await File.ReadAllTextAsync(ImagePath("app-front.tar"), TestContext.Current.CancellationToken));
        Assert.Equal(Source + "\n", await File.ReadAllTextAsync(ImagePath("source-commit"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(ImagePath("stale.tar")));
    }

    [Fact]
    public async Task ArtifactFromAnotherRevision_IsRefusedWithTheScriptsMessage()
    {
        _harness.Write($"{Input}/source-commit", Other + "\n");

        Assert.EndsWith(
            $"Security image artifact revision {Other} does not match pipeline revision {Source}.",
            await FailureAsync(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(ImagePath("")));
    }

    [Fact]
    public async Task SourceParameter_WinsOverBuildSourceVersion()
    {
        _env["BUILD_SOURCEVERSION"] = Other;
        _parameters["source"] = Source;

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(1, Source)]
    [InlineData(2, Other)]
    public async Task ContractOfAnotherSchemaOrSource_IsRefused(int schema, string sourceSha)
    {
        _contract["schema"] = schema;
        _contract["sourceSha"] = sourceSha;

        Assert.EndsWith("The security delivery contract belongs to another source commit.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchiveWithoutALowercaseDigest_IsRefused()
    {
        Digests["app-back.tar.gz"] = Digests["app-back.tar.gz"].ToUpperInvariant();

        Assert.EndsWith("Security image 'app-back.tar.gz' has no valid delivery contract digest.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchiveMissingFromTheContract_IsRefused()
    {
        Digests.Remove("app-front.tar.gz");

        Assert.EndsWith("Security image 'app-front.tar.gz' has no valid delivery contract digest.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TamperedArchive_IsRefusedBeforeAnythingIsUnpacked()
    {
        await File.AppendAllTextAsync(InputPath("app-back.tar.gz"), "x", TestContext.Current.CancellationToken);

        Assert.EndsWith("Security image 'app-back.tar.gz' does not match its delivery contract digest.", await FailureAsync(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(ImagePath("")));
    }

    [Fact]
    public async Task EmptyArchive_IsRefused()
    {
        await File.WriteAllBytesAsync(InputPath("app-front.tar.gz"), [], TestContext.Current.CancellationToken);

        Assert.Contains("app-front.tar.gz is missing or empty.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchiveThatIsNotGzip_FailsTheUnpack()
    {
        await File.WriteAllTextAsync(InputPath("app-back.tar.gz"), "not gzip", TestContext.Current.CancellationToken);
        Digests["app-back.tar.gz"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("not gzip")));

        Assert.Contains("Security image 'app-back.tar.gz' could not be decompressed", await FailureAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../app-back.tar.gz")]
    [InlineData("app-back.tar")]
    [InlineData(".tar.gz")]
    public async Task ImageThatIsNotAPlainArchiveName_IsRefused(string image)
    {
        _parameters["images"] = image;

        Assert.EndsWith($"Image archive '{image}' is not a plain .tar.gz file name.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRevision_IsRefused()
    {
        _env["BUILD_SOURCEVERSION"] = "";

        Assert.EndsWith(
            "BUILD_SOURCEVERSION is not set: the revision the images must come from is unknown.", await FailureAsync(), StringComparison.Ordinal);
    }
}
