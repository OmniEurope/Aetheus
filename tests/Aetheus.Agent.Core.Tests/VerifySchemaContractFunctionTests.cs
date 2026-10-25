// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the former <c>verify-schema-contract.mjs</c> script, with the five cases of
/// <c>tests/js/verify-schema-contract.test.mjs</c> and the two checks the calling step made itself (a
/// missing contract, an empty V-1 reading). Fingerprints are computed here as
/// <c>schema-fingerprint.mjs</c> computes them, independently of the function.
/// </summary>
public sealed class VerifySchemaContractFunctionTests : IDisposable
{
    private static readonly string[] Before = ["20260420134123_InitialCreate"];
    private static readonly string[] After = [.. Before, "20260815060150_AddPipelineBuildNumber"];

    private readonly FunctionTestHarness _harness = new("aetheus-schema-contract");

    public void Dispose() => _harness.Dispose();

    private static object Fingerprint(string[] migrations) => new
    {
        hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', migrations)))),
        migrations
    };

    private static object Honest => new { bootstrap = false, schemaBefore = Fingerprint(Before), schemaAfter = Fingerprint(After) };

    private async Task<(Executors.ExecutorResult Result, string Log)> RunAsync(object? baseline, string[]? before, string[] after)
    {
        if (baseline is not null)
            _harness.Write("ws/.pipeline-artifacts/delivery-contract.json", JsonSerializer.Serialize(new { schema = 2, baseline }));
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["after"] = ".qa-schema-v", ["before"] = ".qa-schema-before" };
        if (before is not null)
            _harness.Write("ws/.qa-schema-before", before.Length == 0 ? string.Empty : string.Join('\n', before) + "\n");
        _harness.Write("ws/.qa-schema-v", string.Join('\n', after) + "\n");
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(parameters)
        };
        var (result, _) = await _harness.RunAsync(new VerifySchemaContractFunction(), env);
        return (result, string.Join('\n', _harness.Log.Select(entry => entry.Line)));
    }

    [Fact]
    public async Task AcceptsAContractWhoseDeclaredPathTheDatabaseReproduces()
    {
        var (result, log) = await RunAsync(Honest, Before, After);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Schema path proven on a live database", log, StringComparison.Ordinal);
        Assert.Contains("(1 migrations) -> ", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesASchemaAfterTheMigrationDoesNotProduce()
    {
        var lying = new { bootstrap = false, schemaBefore = Fingerprint(Before), schemaAfter = Fingerprint([.. After, "20261231000000_NeverExisted"]) };

        var (result, log) = await RunAsync(lying, Before, After);

        Assert.Contains("not the one the contract declares", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("  declared : ", log, StringComparison.Ordinal);
        Assert.Contains("(3 migrations)", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesWhenV1RunsOnAnotherSchemaThanTheContractInherited()
    {
        var (result, log) = await RunAsync(Honest, ["20260101000000_SomethingElse"], After);

        Assert.Contains("starting state", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("requalify", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BootstrapProvesOnlySchemaAfter()
    {
        var (result, log) = await RunAsync(new { bootstrap = true, schemaAfter = Fingerprint(After) }, null, After);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Bootstrap: schemaAfter proven", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesANonBootstrapCandidateThatProvesNoStartingState()
    {
        var (result, _) = await RunAsync(Honest, null, After);

        Assert.EndsWith("A non-bootstrap candidate must prove the schema it migrates from.", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyV1Reading_CountsAsNone()
    {
        var (result, _) = await RunAsync(Honest, [], After);

        Assert.EndsWith("A non-bootstrap candidate must prove the schema it migrates from.", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingContract_IsRefusedWithTheStepsMessage()
    {
        var (result, _) = await RunAsync(null, Before, After);

        Assert.EndsWith("No delivery contract in this workspace; schema states cannot be confronted.", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_SortsTrimsAndDropsEmptyLines()
    {
        var state = VerifySchemaContractFunction.Fingerprint(" b \r\n\na\n");

        Assert.Equal(2, state.Count);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("a\nb"))), state.Hash);
    }
}
