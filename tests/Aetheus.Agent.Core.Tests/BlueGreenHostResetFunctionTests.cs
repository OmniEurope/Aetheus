// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the former <c>bluegreen-host-reset.sh</c> script, run through the real executor as a
/// deployment function against an in-memory Docker daemon and a real state directory: everything of
/// the disposable project removed and proved gone, nothing of anything else, and every identity guard
/// refusing before a single Docker command.
/// </summary>
public sealed class BlueGreenHostResetFunctionTests : IDisposable
{
    private const string Project = "app-nightly";

    private readonly FunctionTestHarness _harness = new("aetheus-bg-reset");
    private readonly FakeFunctionTools _tools = new();
    private readonly FakeDockerHost _docker = new();
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);
    private readonly string _state;

    public BlueGreenHostResetFunctionTests()
    {
        _harness.Executable("bin/docker");
        _env["PATH"] = Path.Combine(_harness.Root, "bin");
        _state = _harness.Workspace + "/state";
        _env["STATE_DIR"] = _state;
        _env["ENV_FILE"] = _state + "/.env";
        _env["SECRETS_FILE"] = _state + "/.secrets";
        _env["COMPOSE_PROJECT"] = Project;
        _tools.OnRun = _docker.AnswerAsync;
        foreach (var file in new[] { ".env", ".secrets", "live-color", "source-commit", "deployment-transaction/journal", "keep-me" })
            _harness.Write("ws/state/" + file, "x");
        _docker.Containers.Add(new FakeContainer("c1", "app-nightly-back-blue", Project, "img"));
        _docker.Containers.Add(new FakeContainer("c2", "app-nightly-migrate", null, "img"));
        _docker.Containers.Add(new FakeContainer("c3", "app-nightly2-back", "app-nightly2", "img"));
        _docker.Volumes.Add(("app-nightly_db-data", Project));
        _docker.Volumes.Add(("app-nightly-git-repos", null));
        _docker.Volumes.Add(("app-prod-db-data", "app-prod"));
        _docker.Networks.Add(("net1", Project));
        _docker.Images.Add(("smoke1", "app-nightly-browser-smoke", "abc"));
    }

    public void Dispose() => _harness.Dispose();

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new BlueGreenHostResetFunction(_tools), _env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, _) = await RunAsync();
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        const string prefix = "Function 'bluegreen-host-reset' failed: ";
        Assert.StartsWith(prefix, result.FailureReason);
        return result.FailureReason![prefix.Length..];
    }

    private IEnumerable<string> LogLines => _harness.Log.Select(entry => entry.Line);

    [Fact]
    public async Task Reset_RemovesEverythingOfTheProjectAndProvesIt()
    {
        _parameters["remove_images"] = "app-nightly-browser-smoke";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["c3"], _docker.Containers.Select(container => container.Id));
        Assert.Equal(["app-prod-db-data"], _docker.Volumes.Select(volume => volume.Name));
        Assert.Empty(_docker.Networks);
        Assert.Empty(_docker.Images);
        Assert.Contains("rm -f c1 c2", _docker.Commands);
        Assert.Contains(">>> Removing the containers of app-nightly", LogLines);
        Assert.Contains(">>> Removing the volumes of app-nightly: app-nightly-git-repos app-nightly_db-data ", LogLines);
        Assert.Contains("Environment app-nightly reset: no container, no volume, no deployment state.", LogLines);
        Assert.Equal(["keep-me"], Directory.EnumerateFileSystemEntries(_state).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Reset_AFreshWorkspaceWithoutAStateDirectoryStillResets()
    {
        Directory.Delete(_state, recursive: true);

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["c3"], _docker.Containers.Select(container => container.Id));
        Assert.Contains("Environment app-nightly reset: no container, no volume, no deployment state.", LogLines);
    }

    [Fact]
    public async Task Reset_WithoutTheImageListLeavesImagesAlone()
    {
        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Single(_docker.Images);
        Assert.DoesNotContain(_docker.Commands, command => command.StartsWith("image", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reset_RefusesAContainerSelectionThatNamesProduction()
    {
        _docker.Containers.Add(new FakeContainer("c4", "app-nightly-prod-copy", Project, "img"));

        Assert.Equal("A container selected for removal names production; refusing to delete anything.", await FailureAsync());
        Assert.Equal(4, _docker.Containers.Count);
        Assert.True(File.Exists(_state + "/.env"));
    }

    [Fact]
    public async Task Reset_RefusesAVolumeSelectionThatNamesProduction()
    {
        _docker.Volumes.Add(("app-nightly-PROD-backup", null));

        Assert.Equal("A volume selected for removal names production; refusing to delete it.", await FailureAsync());
        Assert.Equal(4, _docker.Volumes.Count);
    }

    [Fact]
    public async Task Reset_AContainerThatSurvivesFailsTheProof()
    {
        _docker.Override = command => command.StartsWith("rm -f", StringComparison.Ordinal) ? (0, []) : null;

        Assert.Equal("Containers of app-nightly survived the reset.", await FailureAsync());
    }

    [Fact]
    public async Task Reset_ADockerListingThatFailsIsNotReadAsAnEmptyHost()
    {
        _docker.Override = command => command.StartsWith("volume ls", StringComparison.Ordinal) ? (1, []) : null;

        Assert.Equal("Docker could not list the volumes of app-nightly; refusing to delete anything.", await FailureAsync());
    }

    [Fact]
    public async Task Reset_RequiresDocker()
    {
        _env["PATH"] = Path.Combine(_harness.Root, "empty");

        Assert.Equal("docker is required to reset the environment.", await FailureAsync());
    }

    [Theory]
    [InlineData("state_dir", "{ws}/a/b", "The state directory must be a direct child of the run workspace: {ws}/a/b")]
    [InlineData("state_dir", "{ws}/../state", "The state directory must not contain a traversal: {ws}/../state")]
    [InlineData("state_dir", "/var/lib/app", "The state directory must live in the run workspace ({ws}): /var/lib/app")]
    [InlineData("env_file", "/etc/app.env", "The environment file must live in the state directory.")]
    [InlineData("secrets_file", "{ws}/secrets", "The secrets file must live in the state directory.")]
    [InlineData("compose_project", "App_Nightly", "Not a Compose project name: 'App_Nightly'")]
    [InlineData("compose_project", "app-prod-nightly", "A reset identity contains a production token; refusing to delete anything.")]
    [InlineData("compose_project", "app", "Only a disposable project (*-demo or *-nightly) is reset, not 'app'.")]
    [InlineData("remove_images", "other-browser-smoke", "An image to remove must be a repository of app-nightly, not 'other-browser-smoke'.")]
    public async Task Reset_EveryIdentityGuardRefusesBeforeDocker(string parameter, string value, string message)
    {
        _parameters[parameter] = value.Replace("{ws}", _harness.Workspace, StringComparison.Ordinal);

        Assert.Equal(message.Replace("{ws}", _harness.Workspace, StringComparison.Ordinal), await FailureAsync());
        Assert.Empty(_docker.Commands);
    }

    [Fact]
    public async Task Reset_RequiresTheStateDirectory()
    {
        _env.Remove("STATE_DIR");

        Assert.Equal("STATE_DIR is required", await FailureAsync());
    }
}
