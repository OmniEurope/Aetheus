// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Recette R-90: an image that runs ASP.NET Core 10.0.13 must produce a finding for CVE-2026-111497, fixed by 10.0.14
/// and published by Microsoft on 2026-10-25, while Trivy reported nothing because its database had no advisory for it.
/// The images are real docker-archives (tar of layer tars) read by the real reader; the release metadata is recorded
/// from Microsoft's endpoint, never fetched. What this does NOT prove is the live endpoint and the Trivy run: those
/// are checked by running the function and Trivy themselves against a real image.
/// </summary>
public sealed class ScanDotnetRuntimeCveFunctionTests : IDisposable
{
    private const string ShippedCve = "CVE-2026-111497";

    private readonly FunctionTestHarness _harness = new("aetheus-dotnet-cve");
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal)
    {
        ["images"] = "app-back.tar",
        ["output"] = "analysis/dotnet-runtime-cve.sarif"
    };

    public void Dispose() => _harness.Dispose();

    private string Output => Path.Combine(_harness.Workspace, "analysis", "dotnet-runtime-cve.sarif");

    private static DockerArchiveBuilder.Layer Layer(params (string Path, string Content)[] files) =>
        new(files.ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal));

    private string Image(string name, params DockerArchiveBuilder.Layer[] layers) =>
        DockerArchiveBuilder.Write(Path.Combine(_harness.Workspace, ".analysis-image", name), layers);

    private static (string Path, string Content) AspNetImageFiles(string version) =>
        ($"usr/share/dotnet/shared/Microsoft.AspNetCore.App/{version}/Microsoft.AspNetCore.App.deps.json",
            DockerArchiveBuilder.Deps($"Microsoft.AspNetCore.App.Runtime.linux-x64/{version}"));

    private static (string Path, string Content) FrameworkImageFiles(string version) =>
        ($"usr/share/dotnet/shared/Microsoft.NETCore.App/{version}/Microsoft.NETCore.App.deps.json",
            DockerArchiveBuilder.Deps($"Microsoft.NETCore.App.Runtime.linux-x64/{version}"));

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new RecordedMetadataHandler(respond ?? (_ => Json(DotnetReleaseMetadataFixture.Channel10)));
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters)
        };
        return _harness.RunAsync(new ScanDotnetRuntimeCveFunction(new HttpClient(handler)), env);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private JsonElement[] Results()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Output));
        return [.. document.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().Select(result => result.Clone())];
    }

    private static string RuleId(JsonElement result) => result.GetProperty("ruleId").GetString()!;

    [Fact]
    public async Task AspNetCore10013_ReportsCve2026111497()
    {
        Image("app-back.tar", Layer(("etc/os-release", "ubuntu"), AspNetImageFiles("10.0.13"), FrameworkImageFiles("10.0.13")));

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        var results = Results();
        var finding = Assert.Single(results, item => RuleId(item) == ShippedCve);
        var text = finding.GetProperty("message").GetProperty("text").GetString()!;
        Assert.Contains("Microsoft.AspNetCore.App.Runtime.linux-x64 10.0.13", text, StringComparison.Ordinal);
        Assert.Contains("Microsoft.NETCore.App.Runtime.linux-x64 10.0.13", text, StringComparison.Ordinal);
        Assert.Contains("fixed in 10.0.14", text, StringComparison.Ordinal);
        Assert.Equal("High", finding.GetProperty("properties").GetProperty("severity").GetString());
        Assert.Equal("error", finding.GetProperty("level").GetString());
        Assert.Equal("https://cve.mitre.org/cgi-bin/cvename.cgi?name=CVE-2026-111497",
            JsonDocument.Parse(File.ReadAllText(Output)).RootElement.GetProperty("runs")[0].GetProperty("tool")
                .GetProperty("driver").GetProperty("rules").EnumerateArray()
                .Single(rule => rule.GetProperty("id").GetString() == ShippedCve).GetProperty("helpUri").GetString());
    }

    [Fact]
    public async Task OnlyTheCvesFixedAfterTheInstalledRuntime_AreReported_NotTheOnesAlreadyFixedBy10013()
    {
        Image("app-back.tar", Layer(AspNetImageFiles("10.0.13")));

        await RunAsync();

        // 10.0.14 lists four CVEs; two of them (70197, 102870) were already listed, hence fixed, by 10.0.13.
        Assert.Equal(["CVE-2026-111497", "CVE-2026-114148"], Results().Select(RuleId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AspNetCore10014_ProducesNoFindingButStillWritesTheReport()
    {
        Image("app-back.tar", Layer(AspNetImageFiles("10.0.14"), FrameworkImageFiles("10.0.14")));

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Output));
        Assert.Empty(Results());
    }

    [Fact]
    public async Task OlderRuntime_ReportsEverySecurityReleaseSinceIt()
    {
        Image("app-back.tar", Layer(FrameworkImageFiles("10.0.11")));

        await RunAsync();

        var ids = Results().Select(RuleId).ToList();
        Assert.Contains(ShippedCve, ids);
        Assert.Contains("CVE-2026-96225", ids);
        Assert.Contains("CVE-2026-69439", ids);
    }

    [Fact]
    public async Task SelfContainedApplication_IsReadFromItsRuntimepackEntry()
    {
        Image("app-back.tar", Layer(("app/Aetheus.Back.deps.json", DockerArchiveBuilder.Deps(
            "Aetheus.Back/1.0.0", "runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/10.0.13", "Newtonsoft.Json/13.0.3"))));

        await RunAsync();

        var finding = Assert.Single(Results(), item => RuleId(item) == ShippedCve);
        Assert.Contains("app/Aetheus.Back.deps.json", finding.GetProperty("locations")[0].GetProperty("physicalLocation")
            .GetProperty("artifactLocation").GetProperty("uri").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeReplacedByALaterLayer_IsNotReportedAsTheOneTheImageShips()
    {
        Image("app-back.tar",
            Layer(AspNetImageFiles("10.0.13"), ("usr/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.13/other.dll", "x")),
            Layer(("usr/share/dotnet/shared/Microsoft.AspNetCore.App/.wh.10.0.13", string.Empty), AspNetImageFiles("10.0.14")));

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(Results());
    }

    [Fact]
    public async Task OpaqueDirectoryOfALaterLayer_HidesTheRuntimeBelow()
    {
        Image("app-back.tar",
            Layer(AspNetImageFiles("10.0.13")),
            Layer(("usr/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.13/.wh..wh..opq", string.Empty)));

        await RunAsync();

        Assert.Empty(Results());
    }

    [Fact]
    public async Task GzippedLayers_AreRead()
    {
        Image("app-back.tar", new DockerArchiveBuilder.Layer(
            new Dictionary<string, string> { [AspNetImageFiles("10.0.13").Path] = AspNetImageFiles("10.0.13").Content }, Gzip: true));

        await RunAsync();

        Assert.Contains(ShippedCve, Results().Select(RuleId));
    }

    [Fact]
    public async Task ImageWithoutADotnetRuntime_IsAValidEmptyResult()
    {
        Image("app-back.tar", Layer(("etc/os-release", "alpine"), ("app/Other.deps.json", DockerArchiveBuilder.Deps("Newtonsoft.Json/13.0.3"))));

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(Results());
    }

    [Fact]
    public async Task EachImageGetsItsOwnFinding_WithAnIdentityIndependentOfTheVersion()
    {
        _parameters["images"] = "app-back.tar app-front.tar";
        Image("app-back.tar", Layer(AspNetImageFiles("10.0.13")));
        Image("app-front.tar", Layer(AspNetImageFiles("10.0.12")));

        await RunAsync();

        var identities = Results().Where(item => RuleId(item) == ShippedCve)
            .Select(item => item.GetProperty("partialFingerprints").GetProperty("aetheus/identity").GetString()).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["dotnet-runtime-cve|CVE-2026-111497|app-back.tar", "dotnet-runtime-cve|CVE-2026-111497|app-front.tar"], identities);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task UnreachableMetadata_FailsTheStepAndWritesNoReport(HttpStatusCode status)
    {
        Image("app-back.tar", Layer(AspNetImageFiles("10.0.13")));

        var (result, _) = await RunAsync(_ => new HttpResponseMessage(status));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Contains("release metadata could not be read", result.FailureReason, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task NetworkError_FailsTheStepAndWritesNoReport()
    {
        Image("app-back.tar", Layer(AspNetImageFiles("10.0.13")));

        var (result, _) = await RunAsync(_ => throw new HttpRequestException("name resolution failed"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Output));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"channel-version":"10.0"}""")]
    [InlineData("""{"channel-version":"9.0","releases":[]}""")]
    [InlineData("""{"channel-version":"10.0","releases":[{"release-version":"banana","cve-list":[]}]}""")]
    public async Task MetadataThatIsNotTheChannelsReleaseList_FailsInsteadOfReadingAsNoCve(string body)
    {
        Image("app-back.tar", Layer(AspNetImageFiles("10.0.13")));

        var (result, _) = await RunAsync(_ => Json(body));

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task MissingOrUnreadableImage_Fails()
    {
        var (missing, _) = await RunAsync();
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains("does not exist", missing.FailureReason, StringComparison.Ordinal);

        Directory.CreateDirectory(Path.Combine(_harness.Workspace, ".analysis-image"));
        File.WriteAllText(Path.Combine(_harness.Workspace, ".analysis-image", "app-back.tar"), "this is not a tar archive");
        var (unreadable, _) = await RunAsync();
        Assert.NotEqual(0, unreadable.ExitCode);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task OutputOutsideTheWorkspace_IsRefused()
    {
        _parameters["output"] = "../escape.sarif";
        Image("app-back.tar", Layer(AspNetImageFiles("10.0.13")));

        var (result, _) = await RunAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(_harness.Root, "escape.sarif")));
    }

    [Fact]
    public async Task Metadata_IsFetchedOncePerChannel()
    {
        Image("app-back.tar", Layer(AspNetImageFiles("10.0.13"), FrameworkImageFiles("10.0.13")));
        var requests = new List<Uri>();

        await RunAsync(request =>
        {
            requests.Add(request.RequestUri!);
            return Json(DotnetReleaseMetadataFixture.Channel10);
        });

        Assert.Equal(["https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json"], requests.Select(uri => uri.AbsoluteUri));
    }

    private sealed class RecordedMetadataHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
