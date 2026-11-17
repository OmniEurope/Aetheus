// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The publish-observability step gets the run's own registry credential, masked in the run's
/// logs, in place of any vault value; when none is issued (no project, or a pipeline owner who is not an
/// active administrator) the step is refused with the issuer's reason.</summary>
public sealed class PipelineObservabilityTaskTests
{
    private readonly IPipelineStepTaskBuilder _builder = Substitute.For<IPipelineStepTaskBuilder>();
    private readonly IPackageRunTokenIssuer _issuer = Substitute.For<IPackageRunTokenIssuer>();
    private readonly ISecretMaskingService _masking = Substitute.For<ISecretMaskingService>();
    private readonly Server _server = new() { Id = 3, Name = "runner" };
    private readonly PipelineStepRun _stepRun = new() { Id = 8, StepName = "publish" };
    private readonly PipelineStepDefinition _step = new() { Name = "publish", Type = "publish-observability" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PipelineAnalysisTaskFactory Factory() => new(_builder, _issuer, _masking);

    [Fact]
    public async Task TheMintedRunToken_ReplacesTheVaultToken_AndIsMasked()
    {
        _issuer.MintAsync(42, 5, Arg.Any<CancellationToken>()).Returns(new PackageRunTokenGrant("aetheus-pkg-run.minted", null));
        var legVars = new Dictionary<string, string>
        {
            ["WORKSPACE"] = "/w",
            ["AETHEUS_PACKAGE_TOKEN"] = "aetheus_pat_from_vault",
            ["AETHEUS_NUGET_SIGNING_PFX_PASSWORD"] = "pfx-password"
        };
        HashSet<string> secretKeys = ["AETHEUS_PACKAGE_TOKEN", "AETHEUS_NUGET_SIGNING_PFX_PASSWORD"];

        var error = await Factory().CreateObservabilityTaskAsync(
            42, _server, _stepRun, _step, legVars, secretKeys, projectId: 5, Ct);

        Assert.Null(error);
        _masking.Received(1).RegisterRuntimeSecret(42, "aetheus-pkg-run.minted");
        _builder.Received(1).TrackTypedTask(
            42, _server, _stepRun, _step, "optional-observability",
            Arg.Is<Dictionary<string, string>>(env =>
                env["AETHEUS_PACKAGE_TOKEN"] == "aetheus-pkg-run.minted"
                && env["AETHEUS_WORKING_DIR"] == "/w"
                && env["AETHEUS_NUGET_SIGNING_PFX_PASSWORD"] == "pfx-password"),
            OperationKind.PipelinePublishObservabilityBundle);
    }

    [Fact]
    public async Task AnOwnerWhoIsNotAnActiveAdministrator_RefusesTheStepWithTheReason()
    {
        _issuer.MintAsync(42, 5, Arg.Any<CancellationToken>())
            .Returns(new PackageRunTokenGrant(null, "the pipeline owner does not hold the Admin role."));

        var error = await Factory().CreateObservabilityTaskAsync(
            42, _server, _stepRun, _step, new Dictionary<string, string>(), [], projectId: 5, Ct);

        Assert.NotNull(error);
        Assert.Contains("does not hold the Admin role", error, StringComparison.Ordinal);
        AssertNothingTracked();
    }

    [Fact]
    public async Task APipelineWithoutAProject_IsRefused_WithoutAskingTheIssuer()
    {
        var error = await Factory().CreateObservabilityTaskAsync(
            42, _server, _stepRun, _step, new Dictionary<string, string>(), [], projectId: null, Ct);

        Assert.NotNull(error);
        await _issuer.DidNotReceiveWithAnyArgs().MintAsync(default, default, Ct);
        AssertNothingTracked();
    }

    private void AssertNothingTracked()
    {
        _builder.DidNotReceiveWithAnyArgs().TrackTypedTask(
            default, default!, default!, default!, default!, default!, default);
        _masking.DidNotReceiveWithAnyArgs().RegisterRuntimeSecret(default, default!);
    }
}
