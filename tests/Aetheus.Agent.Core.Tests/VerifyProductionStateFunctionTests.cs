// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the state-directory and secret-zero refusals of
/// the former <c>prod-deploy-prepare.sh</c> script, the ones
/// <c>DeploymentSafetyAuditTests.CanonicalDeployBootstrapsProductionStateWithoutExposingExistingSecrets</c>
/// and <c>ProductionSecretZero_SurvivesAgentReinstall_AndMissingStateFailsClosed</c> pin in the script:
/// a state directory outside <c>/var/lib</c>, nested or traversing, differing canonical and legacy
/// environments, and secret regeneration over persistent state. Path shape is checked on the string;
/// the file checks run on a real directory standing for <c>/var/lib</c> through <see cref="ProductionStatePaths"/>.
/// </summary>
public sealed class VerifyProductionStateFunctionTests : IDisposable
{
    private const string Project = "app-prod";
    private const string Prefix = "Function 'verify-production-state' failed: ";

    private readonly FunctionTestHarness _harness = new("aetheus-prod-state");
    private readonly FakeFunctionTools _tools = new();
    private readonly FakeDockerHost _docker = new();
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);
    private string _stateRoot = ProductionStatePaths.StateRoot;

    public VerifyProductionStateFunctionTests()
    {
        _harness.Executable("bin/docker");
        _env["PATH"] = Path.Combine(_harness.Root, "bin");
        _env["ENV_FILE"] = "/var/lib/app-production/.env-prod";
        _env["COMPOSE_PROJECT"] = Project;
        _tools.OnRun = _docker.AnswerAsync;
    }

    public void Dispose() => _harness.Dispose();

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new VerifyProductionStateFunction(_tools, _stateRoot), _env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, _) = await RunAsync();
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.StartsWith(Prefix, result.FailureReason);
        return result.FailureReason![Prefix.Length..];
    }

    private IEnumerable<string> LogLines => _harness.Log.Select(entry => entry.Line);

    [Theory]
    [InlineData("/var/lib/app/../etc/.env-prod", "Production state directory contains a traversal: /var/lib/app/../etc")]
    [InlineData("/var/lib/app/nested/.env-prod", "Production state directory must be a direct child of /var/lib: /var/lib/app/nested")]
    [InlineData("/etc/app/.env-prod", "Production state directory must live under /var/lib: /etc/app")]
    [InlineData("/var/lib/.env-prod", "Production state directory must live under /var/lib: /var/lib")]
    [InlineData(".env-prod", "Production state directory must live under /var/lib: .")]
    public async Task AStateDirectoryOfTheWrongShape_IsRefused(string envFile, string refusal)
    {
        _env["ENV_FILE"] = envFile;

        Assert.Equal(refusal, await FailureAsync());
        Assert.Empty(_docker.Commands);
    }

    [Fact]
    public async Task AStateDirectoryOtherThanTheDeclaredOne_IsRefused()
    {
        _env["STATE_DIR"] = "/var/lib/other";

        Assert.Equal("ENV_FILE (/var/lib/app-production/.env-prod) does not live in STATE_DIR (/var/lib/other).", await FailureAsync());
    }

    [Fact]
    public async Task NoEnvironmentFile_IsRefusedByName()
    {
        _env["ENV_FILE"] = "";

        Assert.Equal("ENV_FILE is required", await FailureAsync());
    }

    [Fact]
    public async Task AMissingEnvironmentWithPersistentVolumes_RefusesSecretRegeneration()
    {
        _docker.Volumes.Add((Project + "-dp-keys", Project));

        Assert.Equal("Persistent production state exists but /var/lib/app-production/.env-prod is missing.", await FailureAsync());
        Assert.Contains("Secret regeneration is forbidden; no valid dated installation backup was found.", LogLines);
        Assert.Equal(
            [
                "volume inspect app-prod-db-data", "volume inspect app-prod-git-repos", "volume inspect app-prod-dp-keys",
                "volume inspect app-prod-artifacts", "volume inspect app-prod-packages", "ps -a --format {{.Names}}"
            ],
            _docker.Commands);
    }

    [Fact]
    public async Task AMissingEnvironmentWithAProjectContainer_RefusesSecretRegeneration()
    {
        _docker.Containers.Add(new FakeContainer("c1", Project + "-blue-back", Project, "img"));

        Assert.Equal("Persistent production state exists but /var/lib/app-production/.env-prod is missing.", await FailureAsync());
    }

    [Fact]
    public async Task AMissingEnvironmentWithoutAnyState_IsAFirstDeploy()
    {
        _docker.Volumes.Add(("app-prod2-db-data", "app-prod2"));
        _docker.Containers.Add(new FakeContainer("c1", "app-prodx", null, "img"));

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(">>> First deploy: no persistent state of app-prod; the preparation will generate the secrets at /var/lib/app-production/.env-prod.", LogLines);
    }

    [Fact]
    public async Task ADockerThatCannotListContainers_IsNotReadAsAFirstDeploy()
    {
        _docker.Override = command => command.StartsWith("ps", StringComparison.Ordinal) ? (1, []) : null;

        Assert.Equal("docker ps -a failed; persistent production state cannot be ruled out.", await FailureAsync());
    }

    [Fact]
    public async Task AMissingEnvironmentWithoutComposeProject_IsRefusedByName()
    {
        _env["COMPOSE_PROJECT"] = "";

        Assert.Equal("COMPOSE_PROJECT is required", await FailureAsync());
    }

    // The file checks need real files: a writable directory stands in for /var/lib, and the state
    // directory is its direct child, as production requires.
    private string UseRealStateDirectory()
    {
        _stateRoot = _harness.Root.Replace('\\', '/') + "/";
        Directory.CreateDirectory(_stateRoot + "app-production");
        var envFile = _stateRoot + "app-production/.env-prod";
        _env["ENV_FILE"] = envFile;
        return envFile;
    }

    [Fact]
    public async Task AnExistingEnvironment_IsReusedWithoutAskingDocker()
    {
        File.WriteAllText(UseRealStateDirectory(), "APPNAME=app\n");

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(_docker.Commands);
        Assert.Contains(LogLines, line => line.EndsWith(".env-prod exists; its secrets will be reused, never regenerated.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CanonicalAndLegacyEnvironmentsThatDiffer_AreRefused()
    {
        var envFile = UseRealStateDirectory();
        File.WriteAllText(envFile, "DB_PASSWORD=a\n");
        _env["LEGACY_ENV_FILE"] = _harness.Write("legacy/.env-prod", "DB_PASSWORD=b\n");

        Assert.Equal("Canonical and legacy production environments differ; refusing automatic selection.", await FailureAsync());
    }

    [Fact]
    public async Task IdenticalCanonicalAndLegacyEnvironments_AreAccepted()
    {
        var envFile = UseRealStateDirectory();
        File.WriteAllText(envFile, "DB_PASSWORD=a\n");
        _parameters["legacy_env_file"] = _harness.Write("legacy/.env-prod", "DB_PASSWORD=a\n");

        Assert.Equal(0, (await RunAsync()).Result.ExitCode);
    }

    [Fact]
    public async Task AMissingEnvironmentWithALegacyOne_WillBeMigrated()
    {
        UseRealStateDirectory();
        _env["LEGACY_ENV_FILE"] = _harness.Write("legacy/.env-prod", "DB_PASSWORD=a\n");
        _docker.Volumes.Add((Project + "-db-data", Project));

        Assert.Equal(0, (await RunAsync()).Result.ExitCode);
        Assert.Empty(_docker.Commands);
    }

    [Theory]
    [InlineData(".env-prod-20261014T101500Z", true)]
    [InlineData(".env-prod-20261014-101500", false)]
    [InlineData(".env-20261014T101500Z", false)]
    public async Task AMissingEnvironmentWithPersistentState_IsAcceptedOnlyWithADatedCopyToRestore(string copy, bool accepted)
    {
        UseRealStateDirectory();
        _harness.Write("app-production/" + copy, "DB_PASSWORD=a\n");
        _docker.Volumes.Add((Project + "-db-data", Project));

        var (result, _) = await RunAsync();

        Assert.Equal(accepted, result.ExitCode == 0);
    }

    [Fact]
    public async Task AStateDirectoryNestedInTheStandIn_IsRefusedLikeUnderVarLib()
    {
        UseRealStateDirectory();
        _env["ENV_FILE"] = _stateRoot + "app-production/nested/.env-prod";

        Assert.Equal(
            $"Production state directory must be a direct child of {_stateRoot.TrimEnd('/')}: {_stateRoot}app-production/nested",
            await FailureAsync());
    }

    [Theory]
    [InlineData("/var/lib/app/.env-prod", "/var/lib/app")]
    [InlineData("/var/lib/app//.env-prod", "/var/lib/app")]
    [InlineData("/var/lib/app/", "/var/lib")]
    [InlineData(".env-prod", ".")]
    [InlineData("/.env-prod", "/")]
    [InlineData("/", "/")]
    public void Dirname_IsThePosixOne(string path, string expected) =>
        Assert.Equal(expected, ProductionStatePaths.Dirname(path));

    [Theory]
    [InlineData("/var/lib/app", null)]
    [InlineData("/var/lib/a", null)]
    [InlineData("/var/lib/", "Production state directory must live under /var/lib: /var/lib/")]
    [InlineData("/var/libx", "Production state directory must live under /var/lib: /var/libx")]
    [InlineData("/var/lib/a..b", "Production state directory contains a traversal: /var/lib/a..b")]
    public void ShapeRefusal_FollowsTheScriptCase(string directory, string? refusal) =>
        Assert.Equal(refusal, ProductionStatePaths.ShapeRefusal(directory));
}
