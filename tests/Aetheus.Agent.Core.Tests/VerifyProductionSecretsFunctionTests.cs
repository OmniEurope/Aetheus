// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the required-secret check of the former <c>prod-deploy-prepare.sh</c> script, the one
/// <c>DeploymentSafetyAuditTests.ProductionSecretZero_SurvivesAgentReinstall_AndMissingStateFailsClosed</c>
/// and <c>TheDeploymentBootstrapIdentityKey_IsProvisioned_NotJustRequired</c> pin in the script, run
/// through the real executor on a real environment file with the Aetheus key list.
/// </summary>
public sealed class VerifyProductionSecretsFunctionTests : IDisposable
{
    private const string Keys =
        "APPNAME ENV DB_USER DB_PASSWORD JWT_KEY ADMIN_PASSWORD ENCRYPTION_KEY ENCRYPTION_SALT DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY";
    private const string Complete =
        "APPNAME=aetheus\nENV=prod\nDB_USER=aetheus\nDB_PASSWORD=aa\nJWT_KEY=bb\nADMIN_PASSWORD=cc\nENCRYPTION_KEY=dd\nENCRYPTION_SALT=ee\nDEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=ff\n";
    private const string Prefix = "Function 'verify-production-secrets' failed: ";

    private readonly FunctionTestHarness _harness = new("aetheus-prod-secrets");
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal) { ["keys"] = Keys };
    private readonly string _envFile;

    public VerifyProductionSecretsFunctionTests()
    {
        _envFile = _harness.Root.Replace('\\', '/') + "/state/.env-prod";
        _env["ENV_FILE"] = _envFile;
        _harness.Write("state/.env-prod", Complete);
        _harness.Write("state/.env-prod-20261014T101500Z", Complete);
    }

    public void Dispose() => _harness.Dispose();

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new VerifyProductionSecretsFunction(), _env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, _) = await RunAsync();
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.StartsWith(Prefix, result.FailureReason);
        return result.FailureReason![Prefix.Length..];
    }

    [Fact]
    public async Task ACompleteEnvironmentWithADatedCopy_IsAccepted()
    {
        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(_harness.Log, entry => entry.Line == $">>> {_envFile} holds 9 required keys and a dated recovery copy.");
    }

    [Theory]
    [InlineData("DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=ff\n", "")]
    [InlineData("DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=ff\n", "DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=\n")]
    [InlineData("DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=ff\n", "#DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=ff\n")]
    [InlineData("DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=ff\n", " DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=ff\n")]
    public async Task AMissingOrEmptyKey_IsRefusedByName(string line, string replacement)
    {
        _harness.Write("state/.env-prod", Complete.Replace(line, replacement, StringComparison.Ordinal));

        Assert.Equal($"DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY is missing or empty in {_envFile}.", await FailureAsync());
    }

    [Fact]
    public async Task TheFirstMissingKey_InListOrder_IsNamed()
    {
        _harness.Write("state/.env-prod", "APPNAME=aetheus\n");

        Assert.Equal($"ENV is missing or empty in {_envFile}.", await FailureAsync());
    }

    [Fact]
    public async Task AKeyWhosePrefixIsAnotherKey_DoesNotCount()
    {
        _parameters["keys"] = "ENV";
        _harness.Write("state/.env-prod", "ENVIRONMENT=prod\n");

        Assert.Equal($"ENV is missing or empty in {_envFile}.", await FailureAsync());
    }

    [Fact]
    public async Task NoDatedCopy_IsRefused()
    {
        File.Delete(_envFile + "-20261014T101500Z");
        _harness.Write("state/.env-prod-latest", Complete);

        Assert.Equal($"No dated recovery copy of {_envFile} exists next to it.", await FailureAsync());
    }

    [Fact]
    public async Task AMissingEnvironmentFile_IsRefused()
    {
        File.Delete(_envFile);

        Assert.Equal($"{_envFile} is missing.", await FailureAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyKeyList_IsRefusedByTheCatalogBeforeTheFunctionRuns(string keys)
    {
        _parameters["keys"] = keys;

        var (result, _) = await RunAsync();

        Assert.Equal(PipelineFunctionOperationExecutor.InvalidRequest, result.FailureCode);
        Assert.DoesNotContain(_harness.Log, entry => entry.Line.StartsWith(">>>", StringComparison.Ordinal));
    }
}
