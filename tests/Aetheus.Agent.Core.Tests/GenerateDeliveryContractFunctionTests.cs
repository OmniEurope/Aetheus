// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/generate-delivery-contract.mjs</c>. The verifier
/// (<c>verify-delivery-promotion.mjs</c>) recomputes the candidate id from the contract with Node.js, so
/// the expected contracts are the exact files the script wrote for the same inputs (Node.js 24,
/// captured when the function replaced it): a bootstrap in source version mode and a candidate over a
/// production baseline in identity mode, with a mixed-case artifact name that only the
/// <c>localeCompare</c> order places correctly. The refusals are the script's, with its messages, and
/// the cases of <c>delivery-contract.test.mjs</c> that concern the generator.
/// </summary>
public sealed class GenerateDeliveryContractFunctionTests : IDisposable
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Before = "20260101000000_InitialCreate";

    private static readonly string Manifest =
        "{\"schema\":1,\"packages\":[{\"id\":\"Zeta.Pkg\",\"version\":\"2.0.0\",\"enabled\":true,\"sha256\":\"" + new string('C', 64)
        + "\"},{\"id\":\"Aetheus.Telemetry\",\"version\":\"1.2.3+meta\",\"enabled\":true,\"sha256\":\"" + new string('c', 64)
        + "\"},{\"id\":\"Off\",\"version\":\"1\",\"enabled\":false,\"sha256\":\"\"}]}\n";

    private const string ScriptBootstrap = """
        {
          "adapter": {
            "name": "aetheus",
            "version": "1"
          },
          "artifacts": {
            "aetheus-back.tar.gz": "2ec0cfe9c0f501021df290b9dbfdba6466bd5f8136d601b302705b87a74ada83",
            "aetheus-front.tar.gz": "ffe98be8292a598abd64109d1915d113faf6de21cf0ce558c3f46747df05076f",
            "App_b.tar": "2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881"
          },
          "baseline": {
            "bootstrap": true,
            "schemaAfter": {
              "hash": "0a984c9a3f411842af98ca417f34fb8c139c885834d0df5f6438ff50da76aece",
              "migrations": [
                "20260101000000_InitialCreate",
                "20260202000000_AddThing"
              ]
            }
          },
          "candidateId": "8b6a3b9a9c06f4730b9404f5f3deebf8900a770610fe1bf8c392ab663f66e4fd",
          "candidateVersion": "c-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "injection": {
            "manifestSha256": "b8b388f32c762df000063ea37147ec1ec26771a72778e565cfc0c5cdcff80fe6",
            "packages": [
              {
                "id": "Aetheus.Telemetry",
                "sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "version": "1.2.3+meta"
              },
              {
                "id": "Zeta.Pkg",
                "sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "version": "2.0.0"
              }
            ]
          },
          "schema": 2,
          "sourceSha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        }

        """;

    private const string ScriptBaseline = """
        {
          "adapter": {
            "name": "aetheus",
            "version": "1"
          },
          "artifacts": {
            "aetheus-back.tar.gz": "2ec0cfe9c0f501021df290b9dbfdba6466bd5f8136d601b302705b87a74ada83",
            "aetheus-front.tar.gz": "ffe98be8292a598abd64109d1915d113faf6de21cf0ce558c3f46747df05076f",
            "App_b.tar": "2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881"
          },
          "baseline": {
            "bootstrap": false,
            "schemaAfter": {
              "hash": "0a984c9a3f411842af98ca417f34fb8c139c885834d0df5f6438ff50da76aece",
              "migrations": [
                "20260101000000_InitialCreate",
                "20260202000000_AddThing"
              ]
            },
            "schemaBefore": {
              "hash": "13f9560b83d20d5800ed7ddc7db26bd5669f07300b95b3afc6fbdd600f272304",
              "migrations": [
                "20260101000000_InitialCreate"
              ]
            }
          },
          "candidateId": "84482fd65358bada5f90f66216c9e236282a755c4619d015d64bf65d33f2d034",
          "candidateVersion": "c-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-84482fd65358",
          "injection": {
            "manifestSha256": "b8b388f32c762df000063ea37147ec1ec26771a72778e565cfc0c5cdcff80fe6",
            "packages": [
              {
                "id": "Aetheus.Telemetry",
                "sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "version": "1.2.3+meta"
              },
              {
                "id": "Zeta.Pkg",
                "sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "version": "2.0.0"
              }
            ]
          },
          "schema": 2,
          "sourceSha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        }

        """;

    private readonly FunctionTestHarness _harness = new("aetheus-delivery-contract");
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal) { ["BUILD_SOURCEVERSION"] = Commit };
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal)
    {
        ["manifest"] = "manifest.json",
        ["output"] = "out/delivery-contract.json",
        ["adapter_name"] = "aetheus",
        ["adapter_version"] = "1",
        ["candidate_prefix"] = "c-" + Commit,
        ["migrations_dir"] = "mig",
        ["artifacts"] = "aetheus-back.tar.gz App_b.tar aetheus-front.tar.gz"
    };

    public GenerateDeliveryContractFunctionTests()
    {
        _harness.Write("ws/manifest.json", Manifest);
        foreach (var file in new[] { Before + ".cs", Before + ".Designer.cs", "20260202000000_AddThing.cs", "AppDbContextModelSnapshot.cs" })
            _harness.Write("ws/mig/" + file, "// m\n");
        _harness.Write("ws/aetheus-back.tar.gz", "back\n");
        _harness.Write("ws/App_b.tar", "x");
        _harness.Write("ws/aetheus-front.tar.gz", "front\n");
        Directory.CreateDirectory(Path.Combine(_harness.Workspace, "out"));
    }

    public void Dispose() => _harness.Dispose();

    private string Output => Path.Combine(_harness.Workspace, "out", "delivery-contract.json");

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new GenerateDeliveryContractFunction(), _env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, variables) = await RunAsync();
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Empty(variables);
        Assert.False(File.Exists(Output));
        return result.FailureReason!;
    }

    private void ProductionBaseline(string list, string? hash = null)
    {
        _env["DELIVERY_BASELINE_BOOTSTRAP"] = "false";
        _env["DELIVERY_BASELINE_SCHEMA_HASH"] = hash ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(list)));
        _env["DELIVERY_BASELINE_SCHEMA_MIGRATIONS_B64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(list));
    }

    [Fact]
    public async Task Bootstrap_InSourceMode_IsTheContractTheScriptWrote()
    {
        _env["DELIVERY_BASELINE_BOOTSTRAP"] = "true";
        _env["DELIVERY_CANDIDATE_VERSION_MODE"] = "source";

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ScriptBootstrap.ReplaceLineEndings("\n"), await File.ReadAllTextAsync(Output, TestContext.Current.CancellationToken));
        Assert.Equal("8b6a3b9a9c06f4730b9404f5f3deebf8900a770610fe1bf8c392ab663f66e4fd", variables["CANDIDATE_ID"]);
        Assert.Equal("c-" + Commit, variables["CANDIDATE_VERSION"]);
    }

    [Fact]
    public async Task ProductionBaseline_InIdentityMode_IsTheContractTheScriptWrote()
    {
        ProductionBaseline(Before);
        _env["DELIVERY_CANDIDATE_VERSION_MODE"] = "identity";

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ScriptBaseline.ReplaceLineEndings("\n"), await File.ReadAllTextAsync(Output, TestContext.Current.CancellationToken));
        Assert.Equal("c-" + Commit + "-84482fd65358", variables["CANDIDATE_VERSION"]);
    }

    [Fact]
    public async Task CandidateId_IsWhatTheVerifierRecomputes()
    {
        // verify-delivery-promotion.mjs: sha256(JSON.stringify(canonicalize(contract minus id and version))).
        ProductionBaseline(Before);

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        var contract = JsonNode.Parse(await File.ReadAllTextAsync(Output, TestContext.Current.CancellationToken))!.AsObject();
        contract.Remove("candidateId");
        contract.Remove("candidateVersion");
        var recomputed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            ScriptJson.Compact(GenerateDeliveryContractFunction.Canonicalize(contract)!))));
        Assert.Equal(variables["CANDIDATE_ID"], recomputed);
    }

    [Fact]
    public async Task Bootstrap_IsTheDefaultWhenNothingSaysOtherwise()
    {
        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"bootstrap\": true", await File.ReadAllTextAsync(Output, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADifferentInjectionManifest_MakesADifferentCandidate()
    {
        var (_, first) = await RunAsync();
        _harness.Write("ws/manifest.json", "{\"schema\":1,\"packages\":[]}\n");
        _harness.Log.Clear();

        var (_, second) = await RunAsync();

        Assert.NotEqual(first["CANDIDATE_ID"], second["CANDIDATE_ID"]);
    }

    [Fact]
    public async Task NonBootstrapWithoutSchemaHash_IsRefused()
    {
        _env["DELIVERY_BASELINE_BOOTSTRAP"] = "false";

        Assert.EndsWith("A non-bootstrap candidate requires the schema state of the production release.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASchemaListThatDoesNotMatchItsFingerprint_IsRefused()
    {
        ProductionBaseline(Before, new string('0', 64));

        Assert.EndsWith("The production schema list does not match its declared fingerprint.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMigrationsDirectoryWithoutMigration_IsRefused()
    {
        Directory.CreateDirectory(Path.Combine(_harness.Workspace, "empty"));
        _parameters["migrations_dir"] = "empty";

        Assert.Contains("No migrations were found in", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeliveryWithoutDatabase_SealsTheEmptySchemaState()
    {
        _parameters["migrations_dir"] = "-";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        var after = JsonNode.Parse(await File.ReadAllTextAsync(Output, TestContext.Current.CancellationToken))!["baseline"]!["schemaAfter"]!;
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", (string)after["hash"]!);
        Assert.Empty(after["migrations"]!.AsArray());
    }

    [Theory]
    [InlineData("""{"packages":[{"id":"A","version":"1","enabled":true,"sha256":"short"}]}""")]
    [InlineData("""{"packages":[{"id":"A","enabled":true,"sha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}]}""")]
    [InlineData("""{"packages":[{"id":7,"version":"1","enabled":true,"sha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}]}""")]
    public async Task AnEnabledPackageWithoutIdVersionAndDigest_IsRefused(string manifest)
    {
        _harness.Write("ws/manifest.json", manifest);

        Assert.EndsWith("Every enabled integration requires id, exact version and sha256.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADisabledOrMalformedEntry_IsLeftOut()
    {
        _harness.Write("ws/manifest.json", """{"packages":[{"id":"A","enabled":"true"},5]}""");

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"packages\": []", await File.ReadAllTextAsync(Output, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASourceThatIsNotAFullRevision_IsRefusedWithTheUsage()
    {
        _parameters["source_sha"] = "abc";

        Assert.Contains("Usage: generate-delivery-contract", await FailureAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("artifacts", " ")]
    [InlineData("adapter_name", "")]
    public async Task AnEmptyRequiredArgument_IsRefusedBeforeRunning(string parameter, string value)
    {
        _parameters[parameter] = value;

        var (result, _) = await RunAsync();

        Assert.Equal(PipelineFunctionOperationExecutor.InvalidRequest, result.FailureCode);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task ANullPackageEntry_IsRefusedAsTheScriptThrewOnIt()
    {
        _harness.Write("ws/manifest.json", """{"packages":[null]}""");

        Assert.EndsWith("The integration manifest holds a null package entry.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MjAyNjAxMDEwMDAwMDBfSW5pdGlhbENyZWF0ZQ==", Before)]
    [InlineData("MjAyNjAxMDEwMDAwMDBfSW5pdGlhbENyZWF0ZQ", Before)]
    [InlineData("YQpi\n", "a|b")]
    [InlineData("", "")]
    public void BaselineMigrations_DecodeAsNodeBufferDoes(string base64, string expected)
    {
        var inputs = DeliveryContractInputsFor(base64);

        Assert.Equal(expected, string.Join('|', inputs.BaselineMigrations()));
    }

    private DeliveryContractInputs DeliveryContractInputsFor(string base64)
    {
        var context = new PipelineFunctionContext(
            PipelineFunctionCatalog.GenerateDeliveryContract, _harness.Workspace, _parameters,
            new Dictionary<string, string> { ["BUILD_SOURCEVERSION"] = Commit, ["DELIVERY_BASELINE_SCHEMA_MIGRATIONS_B64"] = base64 },
            (_, _) => Task.CompletedTask);
        return DeliveryContractInputs.Read(context);
    }
}
