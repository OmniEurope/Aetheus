// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the leftover-transaction guard of the former <c>release-fast-deploy.sh</c> script, run
/// through the real executor on a real state directory: the same decisions
/// <c>DeploymentSafetyAuditTests.FastRelease_ClosesACommittedButUnclosedTransactionInsteadOfRefusingForever</c>
/// pins in the script (a committed transaction that went live is closed, any other leftover refused).
/// </summary>
public sealed class SettleFastReleaseTransactionFunctionTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Prefix = "Function 'settle-fast-release-transaction' failed: ";

    private readonly FunctionTestHarness _harness = new("aetheus-settle-fast");
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);
    private readonly string _state;
    private readonly string _transaction;

    public SettleFastReleaseTransactionFunctionTests()
    {
        _state = _harness.Root.Replace('\\', '/') + "/state";
        _transaction = _state + "/fast-deployment-transaction";
        _env["ENV_FILE"] = _state + "/.env-prod";
        _harness.Write("state/.env-prod", "APPNAME=app\n");
        _harness.Write("state/live-color", "green\n");
    }

    public void Dispose() => _harness.Dispose();

    private void Transaction(string? state, string? idle, string? commit)
    {
        Directory.CreateDirectory(_transaction);
        if (state is not null) _harness.Write("state/fast-deployment-transaction/state", state + "\n");
        if (idle is not null) _harness.Write("state/fast-deployment-transaction/idle", idle + "\n");
        if (commit is not null) _harness.Write("state/fast-deployment-transaction/source-commit", commit + "\n");
    }

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new SettleFastReleaseTransactionFunction(), _env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, _) = await RunAsync();
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.StartsWith(Prefix, result.FailureReason);
        return result.FailureReason![Prefix.Length..];
    }

    private IEnumerable<string> LogLines => _harness.Log.Select(entry => entry.Line);

    [Fact]
    public async Task NoLeftoverTransaction_IsAccepted()
    {
        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($">>> No unfinished fast deployment transaction at {_transaction}.", LogLines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Commit)]
    public async Task ACommittedTransactionThatWentLive_IsClosed(string? recorded)
    {
        Transaction(SettleFastReleaseTransactionFunction.CommittedPendingRelease, "green", Commit);
        if (recorded is not null)
            _harness.Write("state/source-commit", recorded + "\n");

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(_transaction));
        Assert.Contains($">>> Closing the fast deployment transaction of {Commit}: it went live as green and was never committed.", LogLines);
    }

    [Fact]
    public async Task ACommittedTransactionWithoutCommit_IsClosedAsAnUnknownCommit()
    {
        Transaction(SettleFastReleaseTransactionFunction.CommittedPendingRelease, "green", null);

        Assert.Equal(0, (await RunAsync()).Result.ExitCode);
        Assert.Contains(">>> Closing the fast deployment transaction of an unknown commit: it went live as green and was never committed.", LogLines);
    }

    [Theory]
    [InlineData("HOST_PREPARED", "green", "state=HOST_PREPARED, candidate=green")]
    [InlineData("HOST_COMMITTED_PENDING_RELEASE", "blue", "state=HOST_COMMITTED_PENDING_RELEASE, candidate=blue")]
    [InlineData(null, null, "state=?, candidate=?")]
    public async Task AnyOtherLeftover_IsRefusedAndKept(string? state, string? idle, string described)
    {
        Transaction(state, idle, Commit);

        Assert.Equal(
            $"an unfinished fast deployment transaction already exists at {_transaction} ({described}, live=green).",
            await FailureAsync());
        Assert.True(Directory.Exists(_transaction));
    }

    [Fact]
    public async Task ACommittedTransactionOfAnotherCommitThanTheRecordedOne_IsRefused()
    {
        Transaction(SettleFastReleaseTransactionFunction.CommittedPendingRelease, "green", Commit);
        _harness.Write("state/source-commit", "fedcba9876543210fedcba9876543210fedcba98\n");

        Assert.StartsWith("an unfinished fast deployment transaction already exists", await FailureAsync());
        Assert.True(Directory.Exists(_transaction));
    }

    [Fact]
    public async Task ATransactionPathThatIsAFile_IsRefused()
    {
        _harness.Write("state/fast-deployment-transaction", "x");

        Assert.Equal(
            $"an unfinished fast deployment transaction already exists at {_transaction} (state=?, candidate=?, live=green).",
            await FailureAsync());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("purple")]
    [InlineData("")]
    public async Task ALiveColourThatIsNotBlueOrGreen_IsRefused(string colour)
    {
        _harness.Write("state/live-color", colour);

        Assert.Equal("Fast release requires an existing blue-green live colour.", await FailureAsync());
    }

    [Fact]
    public async Task AMissingEnvironmentFile_IsRefused()
    {
        File.Delete(_state + "/.env-prod");

        Assert.Equal($"Fast release requires an existing production installation and {_state}/.env-prod.", await FailureAsync());
    }

    [Fact]
    public async Task NoEnvironmentFile_IsRefusedByName()
    {
        _env["ENV_FILE"] = "";

        Assert.Equal("Required variable is missing: ENV_FILE", await FailureAsync());
    }

    [Fact]
    public async Task TheParameter_WinsOverTheVariable()
    {
        _env["ENV_FILE"] = "/nowhere/.env-prod";
        _parameters["env_file"] = _state + "/.env-prod";

        Assert.Equal(0, (await RunAsync()).Result.ExitCode);
    }
}
