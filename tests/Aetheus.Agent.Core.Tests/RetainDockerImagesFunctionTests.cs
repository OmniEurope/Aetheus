// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/retain-docker-images.sh</c>, run through the real executor as a
/// deployment function against an in-memory Docker daemon: protected and recent tags kept, the rest
/// removed (or listed in dry run), the re-checks right before a removal, the container that vanished or
/// cannot be inspected, the refusals, and the host-wide lock.
/// </summary>
public sealed class RetainDockerImagesFunctionTests : IDisposable
{
    private readonly FunctionTestHarness _harness = new("aetheus-retain-images");
    private readonly FakeFunctionTools _tools = new();
    private readonly FakeDockerHost _docker = new();
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);

    public RetainDockerImagesFunctionTests()
    {
        _env["PATH"] = Path.Combine(_harness.Root, "bin");
        _env["TMPDIR"] = Directory.CreateDirectory(Path.Combine(_harness.Root, "tmp")).FullName;
        _tools.OnRun = _docker.AnswerAsync;
        _parameters["repositories"] = "app-back";
        _docker.Images.Add(("sha-new", "app-back", "v4"));
        _docker.Images.Add(("sha-active", "app-back", "v3"));
        _docker.Images.Add(("sha-active", "app-back", "latest"));
        _docker.Images.Add(("sha-dangling", "app-back", "<none>"));
        _docker.Images.Add(("sha-previous", "app-back", "v2"));
        _docker.Images.Add(("sha-old", "app-back", "v1"));
        _docker.Containers.Add(new FakeContainer("container-1", "app-blue-back", "app", "sha-active"));
    }

    public void Dispose() => _harness.Dispose();

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new RetainDockerImagesFunction(_tools), _env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, _) = await RunAsync();
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        var prefix = "Function 'retain-docker-images' failed: ";
        Assert.StartsWith(prefix, result.FailureReason);
        return result.FailureReason![prefix.Length..];
    }

    private List<string> Decisions => [.. _harness.Log.Select(entry => entry.Line)
        .Where(line => line.Split(' ')[0] is "KEEP" or "WOULD_REMOVE" or "REMOVE" or "SKIP")];

    [Fact]
    public async Task DryRun_KeepsProtectedAndRecentAndListsTheRest()
    {
        _env["AETHEUS_IMAGE_RETENTION_DRY_RUN"] = "true";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                "KEEP recent app-back:v4 sha-new",
                "KEEP protected app-back:v3 sha-active",
                "WOULD_REMOVE app-back:v2 sha-previous",
                "WOULD_REMOVE app-back:v1 sha-old"
            ],
            Decisions);
        Assert.Contains(">>> Docker image retention: dryRun=true keep=2", _harness.Log.Select(entry => entry.Line));
        Assert.Contains("    sha-active", _harness.Log.Select(entry => entry.Line));
        Assert.DoesNotContain(_docker.Commands, command => command.StartsWith("image rm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retention_RemovesEachUnprotectedOlderTagAfterReResolvingIt()
    {
        _parameters["keep"] = "3";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                "KEEP recent app-back:v4 sha-new",
                "KEEP protected app-back:v3 sha-active",
                "KEEP recent app-back:v2 sha-previous",
                "REMOVE app-back:v1 sha-old"
            ],
            Decisions);
        var inspect = _docker.Commands.IndexOf("image inspect --format {{.Id}} app-back:v1");
        Assert.True(inspect >= 0 && inspect < _docker.Commands.IndexOf("image rm app-back:v1"));
        Assert.Equal(["sha-new", "sha-active", "sha-active", "sha-dangling", "sha-previous"], _docker.Images.Select(image => image.Id));
    }

    [Fact]
    public async Task Retention_KeepsAnImageAContainerStartedOnBeforeItsRemoval()
    {
        _docker.Override = command =>
        {
            if (command == "image inspect --format {{.Id}} app-back:v2" && _docker.Containers.Count == 1)
                _docker.Containers.Add(new FakeContainer("container-2", "app-green-back", "app", "sha-previous"));
            return null;
        };

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("KEEP newly-protected app-back:v2 sha-previous", Decisions);
        Assert.Contains("REMOVE app-back:v1 sha-old", Decisions);
        Assert.Contains(_docker.Images, image => image.Id == "sha-previous");
    }

    [Fact]
    public async Task Retention_RefusesATagThatMovedSinceTheInventory()
    {
        _docker.Override = command => command == "image inspect --format {{.Id}} app-back:v2" ? (0, ["sha-moved"]) : null;

        Assert.Equal("Tag 'app-back:v2' changed from sha-previous to sha-moved; refusing stale deletion.", await FailureAsync());
        Assert.DoesNotContain(_docker.Commands, command => command.StartsWith("image rm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retention_RefusesAContainerThatExistsButCannotBeInspected()
    {
        _docker.Override = command => command.StartsWith("inspect --format {{.Image}}", StringComparison.Ordinal) ? (1, []) : null;

        Assert.Equal("Could not inspect container 'container-1'; refusing image retention.", await FailureAsync());
        Assert.DoesNotContain(_docker.Commands, command => command.StartsWith("image rm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retention_SkipsAContainerThatVanishedBetweenListingAndInspection()
    {
        _docker.Override = command => command switch
        {
            "inspect --format {{.Image}} container-1" => (1, []),
            "ps -aq --no-trunc --filter id=container-1" => (0, []),
            _ => null
        };

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SKIP vanished container container-1", Decisions);
        Assert.Contains("REMOVE app-back:v1 sha-old", Decisions);
    }

    [Fact]
    public async Task Retention_RefusesWhenContainersCannotBeEnumerated()
    {
        _docker.Override = command => command == "ps -aq --no-trunc" ? (1, []) : null;

        Assert.Equal("Could not enumerate containers; refusing image retention.", await FailureAsync());
    }

    [Theory]
    [InlineData("dry_run", "yes", "AETHEUS_IMAGE_RETENTION_DRY_RUN must be true or false")]
    [InlineData("keep", "two", "AETHEUS_IMAGE_RETENTION_KEEP must be a positive integer")]
    [InlineData("keep", "1", "Image retention must keep at least active and N-1")]
    [InlineData("repositories", "-app", "Managed repository must not start with '-': '-app'")]
    [InlineData("repositories", "app;rm", "Unsafe managed repository 'app;rm'")]
    public async Task Retention_RefusesWhatTheScriptRefused(string parameter, string value, string message)
    {
        _parameters[parameter] = value;

        Assert.Equal(message, await FailureAsync());
        Assert.DoesNotContain(_docker.Commands, command => command.StartsWith("image rm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retention_RefusesToRunBesideAnotherRetention()
    {
        await using var held = new FileStream(
            Path.Combine(_env["TMPDIR"], "aetheus-image-retention.lock"), FileMode.Create, FileAccess.Write, FileShare.None);

        Assert.Equal("Another image-retention operation is active; refusing concurrent retention.", await FailureAsync());
        Assert.Empty(_docker.Commands);
    }

    [Fact]
    public void RetainDockerImages_IsADeploymentFunction() =>
        Assert.Equal(OperationKind.PipelineDeploymentFunction, PipelineFunctionCatalog.RetainDockerImages.Operation);
}
