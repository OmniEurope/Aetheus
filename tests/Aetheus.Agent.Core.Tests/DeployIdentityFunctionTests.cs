// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/deploy-identity.sh</c>, run through the real executor: the slug and
/// image repositories derived from the project name, the helper directory and its refusals, with the
/// script's messages.
/// </summary>
public sealed class DeployIdentityFunctionTests : IDisposable
{
    private readonly FunctionTestHarness _harness = new("aetheus-deploy-identity");
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal) { ["BUILD_PROJECTNAME"] = "Aetheus" };
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);

    public void Dispose() => _harness.Dispose();

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new DeployIdentityFunction(), _env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, variables) = await RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Empty(variables);
        return result.FailureReason!;
    }

    [Fact]
    public async Task Identity_IsNamedAfterTheProjectWithTheInstallerHelpersByDefault()
    {
        _env["AGENT_HELPERS_DIR"] = "";

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("aetheus", variables["PROJECT_SLUG"]);
        Assert.Equal("aetheus-back", variables["BACK_IMAGE_REPO"]);
        Assert.Equal("aetheus-front", variables["FRONT_IMAGE_REPO"]);
        Assert.Equal("/usr/local/lib/aetheus", variables["AGENT_HELPERS_DIR"]);
    }

    [Theory]
    [InlineData("My Great App", "my-great-app")]
    [InlineData("--Weird__Name!!2--", "weird-name-2")]
    [InlineData("Équipe Été", "quipe-t")]
    [InlineData("2026 Release", "2026-release")]
    public void Slug_FollowsTheScriptsTrAndSed(string name, string slug) =>
        Assert.Equal(slug, DeployIdentityFunction.Slug(name));

    [Fact]
    public async Task Parameters_WinOverTheScriptVariables()
    {
        _env["AGENT_HELPERS_DIR"] = "/ignored";
        _parameters["project_name"] = "Other App";
        _parameters["helpers_dir"] = "/opt/helpers";

        var (_, variables) = await RunAsync();

        Assert.Equal("other-app-back", variables["BACK_IMAGE_REPO"]);
        Assert.Equal("/opt/helpers", variables["AGENT_HELPERS_DIR"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("@@@")]
    public async Task ProjectNameWithoutUsableCharacter_IsRefused(string name)
    {
        _env["BUILD_PROJECTNAME"] = name;

        Assert.EndsWith(
            "BUILD_PROJECTNAME is missing or has no usable character; the images cannot be named.",
            await FailureAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/usr/../etc", "AGENT_HELPERS_DIR must not contain a traversal: /usr/../etc")]
    [InlineData("../x", "AGENT_HELPERS_DIR must not contain a traversal: ../x")]
    [InlineData("relative/dir", "AGENT_HELPERS_DIR must be an absolute path: relative/dir")]
    public async Task HelperDirectoryNotAbsoluteOrTraversing_IsRefusedFirst(string helpers, string message)
    {
        _env["AGENT_HELPERS_DIR"] = helpers;
        _env["BUILD_PROJECTNAME"] = "";

        Assert.EndsWith(message, await FailureAsync(), StringComparison.Ordinal);
    }
}
