// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/candidate/export-baseline-identity.sh</c>, run through the real
/// executor on a temp workspace with Git answered by a fake: the bootstrap attestation, the schema state
/// of a current contract, the reconstruction of a legacy one from the Git tree of its deployed revision,
/// and the script's refusals with its messages.
/// </summary>
public sealed class ExportBaselineIdentityFunctionTests : IDisposable
{
    private const string Sha = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
    private const string ContractPath = "ws/.delivery-baseline/.pipeline-artifacts/delivery-contract.json";
    private const string MigrationsDir = "src/App/Data/Migrations";

    private readonly FunctionTestHarness _harness = new("aetheus-baseline-identity");
    private readonly FakeFunctionTools _tools = new();
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);

    public ExportBaselineIdentityFunctionTests()
    {
        _env["PATH"] = Path.Combine(_harness.Root, "bin");
        _tools.OnRun = Answer;
    }

    public void Dispose() => _harness.Dispose();

    private bool CommitPresent { get; set; } = true;
    private int ListExit { get; set; }
    private string[] Tree { get; set; } =
    [
        MigrationsDir + "/20260102_Second.cs",
        MigrationsDir + "/20260102_Second.Designer.cs",
        MigrationsDir + "/20260101_First.cs",
        MigrationsDir + "/20260101_First.Designer.cs",
        MigrationsDir + "/AppDbContextModelSnapshot.cs",
        MigrationsDir + "/README.md"
    ];

    private Task<(int, string[])> Answer(ProcessStartInfo process, CancellationToken _)
    {
        var arguments = string.Join(' ', process.ArgumentList);
        (int, string[]) answer = arguments switch
        {
            $"cat-file -e {Sha}^{{commit}}" => (CommitPresent ? 0 : 1, []),
            $"ls-tree --name-only {Sha} {MigrationsDir}/" => (ListExit, ListExit == 0 ? Tree : []),
            _ => (127, [])
        };
        return Task.FromResult(answer);
    }

    private IEnumerable<string> GitCommands => _tools.Runs.Select(run => string.Join(' ', run.ArgumentList));

    private void WriteContract(string json) => _harness.Write(ContractPath, json);

    private static string Fingerprint(params string[] migrations) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', migrations))));

    private static string Base64(params string[] migrations) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', migrations)));

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new ExportBaselineIdentityFunction(_tools), _env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, variables) = await RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Empty(variables);
        return result.FailureReason!;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task NoRestoredContract_AttestsABootstrap(string? content)
    {
        if (content is not null)
            WriteContract(content);

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("true", variables["DELIVERY_BASELINE_BOOTSTRAP"]);
        Assert.Equal(string.Empty, variables["DELIVERY_BASELINE_SOURCE_SHA"]);
        Assert.Equal(string.Empty, variables["DELIVERY_BASELINE_SCHEMA_HASH"]);
        Assert.Equal(string.Empty, variables["DELIVERY_BASELINE_SCHEMA_MIGRATIONS_B64"]);
        Assert.Empty(_tools.Runs);
    }

    [Fact]
    public async Task CurrentContract_PublishesItsSchemaStateWithoutGit()
    {
        WriteContract("""{"schema":2,"sourceSha":""" + "\"" + Sha + "\"" + ""","baseline":{"schemaAfter":{"hash":"feed","migrations":["M1","M2"]}}}""");

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("false", variables["DELIVERY_BASELINE_BOOTSTRAP"]);
        Assert.Equal(Sha, variables["DELIVERY_BASELINE_SOURCE_SHA"]);
        Assert.Equal("feed", variables["DELIVERY_BASELINE_SCHEMA_HASH"]);
        Assert.Equal(Base64("M1", "M2"), variables["DELIVERY_BASELINE_SCHEMA_MIGRATIONS_B64"]);
        Assert.Empty(_tools.Runs);
    }

    [Theory]
    [InlineData("""{"hash":"feed","migrations":[]}""")]
    [InlineData("""{"hash":"feed"}""")]
    [InlineData("""{"hash":"feed","migrations":"M1"}""")]
    public async Task CurrentContractWithoutMigrations_IsRefused(string schemaAfter)
    {
        WriteContract($$$"""{"sourceSha":"{{{Sha}}}","baseline":{"schemaAfter":{{{schemaAfter}}}}}""");

        Assert.EndsWith("The production schema state could not be established.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MigrationsDir)]
    [InlineData(MigrationsDir + "/")]
    public async Task LegacyContract_IsRebuiltFromTheDeployedTree(string directory)
    {
        WriteContract($$$"""{"schema":1,"sourceSha":"{{{Sha}}}"}""");
        _parameters["migrations_dir"] = directory;

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [$"cat-file -e {Sha}^{{commit}}", $"ls-tree --name-only {Sha} {MigrationsDir}/"], GitCommands);
        Assert.All(_tools.Runs, run => Assert.Equal(_harness.Workspace, run.WorkingDirectory));
        Assert.Equal("false", variables["DELIVERY_BASELINE_BOOTSTRAP"]);
        Assert.Equal(Sha, variables["DELIVERY_BASELINE_SOURCE_SHA"]);
        Assert.Equal(Fingerprint("20260101_First", "20260102_Second"), variables["DELIVERY_BASELINE_SCHEMA_HASH"]);
        Assert.Equal(Base64("20260101_First", "20260102_Second"), variables["DELIVERY_BASELINE_SCHEMA_MIGRATIONS_B64"]);
        Assert.Contains(_harness.Log, entry => entry.Line ==
            $"Reconstructed the schema state of legacy contract schema 1 from deployed revision {Sha}: 2 migrations.");
    }

    [Fact]
    public async Task ContractWithAnEmptyHash_IsTreatedAsLegacy()
    {
        WriteContract("""{"sourceSha":""" + "\"" + Sha + "\"" + ""","baseline":{"schemaAfter":{"hash":"","migrations":["M1"]}}}""");
        _parameters["migrations_dir"] = MigrationsDir;

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Fingerprint("20260101_First", "20260102_Second"), variables["DELIVERY_BASELINE_SCHEMA_HASH"]);
        Assert.Contains(_harness.Log, entry => entry.Line.Contains("legacy contract schema undefined", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"schema":1}""")]
    [InlineData("""{"schema":1,"sourceSha":"abc"}""")]
    [InlineData("""{"schema":1,"sourceSha":"abcdefabcdefabcdefabcdefabcdefabcdefabcdx"}""")]
    [InlineData("[]")]
    public async Task LegacyContractWithoutARevision_IsRefused(string json)
    {
        WriteContract(json);
        _parameters["migrations_dir"] = MigrationsDir;

        Assert.EndsWith(
            "The deployed contract names no source revision; nothing to reconstruct from.", await FailureAsync(), StringComparison.Ordinal);
        Assert.Empty(_tools.Runs);
    }

    [Fact]
    public async Task LegacyContractWithoutMigrationsDir_NamesTheMissingParameter()
    {
        WriteContract($$$"""{"schema":1,"sourceSha":"{{{Sha}}}"}""");

        Assert.EndsWith(
            "The deployed contract has no schema state (baseline.schemaAfter) and no migrations_dir is set to reconstruct it from.",
            await FailureAsync(), StringComparison.Ordinal);
        Assert.Empty(_tools.Runs);
    }

    [Fact]
    public async Task DeployedRevisionOutsideTheCheckout_AsksForMoreHistory()
    {
        WriteContract($$$"""{"schema":1,"sourceSha":"{{{Sha}}}"}""");
        _parameters["migrations_dir"] = MigrationsDir;
        CommitPresent = false;

        Assert.EndsWith(
            $"The deployed revision {Sha} is not in this checkout, so the schema state of the legacy contract cannot be "
            + "reconstructed. Raise AETHEUS_GIT_HISTORY_DEPTH until it reaches that revision.",
            await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployedTreeWithoutMigrations_IsRefused()
    {
        WriteContract($$$"""{"schema":1,"sourceSha":"{{{Sha}}}"}""");
        _parameters["migrations_dir"] = MigrationsDir;
        Tree = [MigrationsDir + "/README.md", MigrationsDir + "/AppDbContextModelSnapshot.cs"];

        Assert.EndsWith($"No migrations found at deployed revision {Sha}.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedTreeListing_IsRefused()
    {
        WriteContract($$$"""{"schema":1,"sourceSha":"{{{Sha}}}"}""");
        _parameters["migrations_dir"] = MigrationsDir;
        ListExit = 128;

        Assert.EndsWith(
            $"git ls-tree of {MigrationsDir}/ at deployed revision {Sha} failed.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContractThatIsNotJson_IsRefusedWithItsPath()
    {
        WriteContract("{not json");

        Assert.Contains(
            "The deployed delivery contract .delivery-baseline/.pipeline-artifacts/delivery-contract.json is not valid JSON",
            await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContractParameter_ReadsAnotherPath()
    {
        _harness.Write("ws/baseline/contract.json", """{"sourceSha":"s","baseline":{"schemaAfter":{"hash":"h","migrations":["A"]}}}""");
        _parameters["contract"] = "baseline/contract.json";

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("h", variables["DELIVERY_BASELINE_SCHEMA_HASH"]);
        Assert.Equal("s", variables["DELIVERY_BASELINE_SOURCE_SHA"]);
    }

    [Fact]
    public async Task ContractWithoutSourceSha_PublishesAnEmptyRevision()
    {
        WriteContract("""{"baseline":{"schemaAfter":{"hash":"h","migrations":["A",null]}}}""");

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, variables["DELIVERY_BASELINE_SOURCE_SHA"]);
        Assert.Equal(Base64("A", ""), variables["DELIVERY_BASELINE_SCHEMA_MIGRATIONS_B64"]);
    }
}
