// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using System.Text.Json;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Components.Settings;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// "Run to end" (PLAN-002 step D, local mirror proof): offered and accepted only when the platform
/// setting allows it, refused by the backend otherwise, and a run past a failure publishes no release.
/// The scheduling side (steps continuing, final status) is covered in <c>PipelineRunServiceTests</c>.
/// </summary>
public sealed class PipelineRunToEndTests
{
    private readonly IPipelineService _pipelines = Substitute.For<IPipelineService>();
    private readonly IPipelineRunService _runs = Substitute.For<IPipelineRunService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IPipelineRunToEndPolicy _policy = Substitute.For<IPipelineRunToEndPolicy>();
    private readonly PipelinesController _controller;

    public PipelineRunToEndTests()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(),
            Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(true);
        _pipelines.GetPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineDto { Id = 1, Name = "ci", YamlDefinition = "name: ci" });
        _runs.PrepareRunAsync(1, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRunPreparation { PipelineId = 1, YamlSnapshot = "name: ci", TargetServerIds = [] });
        _runs.ResolveRunParametersAsync(Arg.Any<PipelineRunPreparation>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<PipelineRunPreparation>(0));
        _runs.TriggerPreparedRunAsync(Arg.Any<PipelineRunPreparation>(), Arg.Any<Dictionary<string, string>?>(),
                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new PipelineRunDto { Id = 10, PipelineId = 1, Status = PipelineStatus.Running });
        _controller = new PipelinesController(
            _pipelines, _runs, Substitute.For<IPipelineApprovalService>(), Substitute.For<IPipelineArtifactService>(),
            Substitute.For<IPipelineWebhookService>(), new PipelineOwnerAuthorization(_pipelines, _authz), _authz, _policy)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
                }
            }
        };
    }

    [Fact]
    public async Task TriggerRun_RunToEndWhileTheSettingIsOff_IsRefusedWithItsReason_AndNothingStarts()
    {
        _policy.IsAllowedAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await _controller.TriggerRun(
            1, new PipelineRunRequest { RunToEnd = true }, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal(PipelineRunToEndPolicy.RefusalMessage, Assert.IsType<ApiError>(refused.Value).Message);
        await _runs.DidNotReceiveWithAnyArgs().PrepareRunAsync(default, default, TestContext.Current.CancellationToken);
        await _runs.DidNotReceiveWithAnyArgs().TriggerPreparedRunAsync(default!, default, default, TestContext.Current.CancellationToken, default);
    }

    [Fact]
    public async Task TriggerRun_RunToEndWhileTheSettingIsOn_LaunchesThePreparationInThatMode()
    {
        _policy.IsAllowedAsync(Arg.Any<CancellationToken>()).Returns(true);

        var response = await _controller.TriggerRun(
            1, new PipelineRunRequest { RunToEnd = true }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(response.Result);
        await _runs.Received(1).TriggerPreparedRunAsync(
            Arg.Is<PipelineRunPreparation>(preparation => preparation.RunToEnd),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task TriggerRun_WithoutRunToEnd_NeverAsksTheSetting_AndLaunchesANormalRun()
    {
        await _controller.TriggerRun(1, new PipelineRunRequest(), TestContext.Current.CancellationToken);

        await _policy.DidNotReceiveWithAnyArgs().IsAllowedAsync(TestContext.Current.CancellationToken);
        await _runs.Received(1).TriggerPreparedRunAsync(
            Arg.Is<PipelineRunPreparation>(preparation => !preparation.RunToEnd),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetRunOptions_SaysWhatTheSettingAllows(bool allowed)
    {
        _policy.IsAllowedAsync(Arg.Any<CancellationToken>()).Returns(allowed);

        var response = await _controller.GetRunOptions(TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(allowed, Assert.IsType<PipelineRunOptionsDto>(ok.Value).RunToEndAllowed);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    public async Task Policy_IsOffUnlessTheSettingSaysTrue(string? stored, bool expected)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingValueAsync(PipelineRunToEndSetting.Key, Arg.Any<CancellationToken>()).Returns(stored);

        Assert.Equal(expected, await new PipelineRunToEndPolicy(settings).IsAllowedAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Settings_RunToEndAcceptsOnlyTrueOrFalse()
    {
        var repo = Substitute.For<ISettingsRepository>();
        var sut = new SettingsService(repo, Substitute.For<IEncryptionService>(), Substitute.For<IAuditService>(), TimeProvider.System);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            sut.UpdateSettingAsync(PipelineRunToEndSetting.Key, "on", TestContext.Current.CancellationToken));
        await repo.DidNotReceiveWithAnyArgs().AddSettingAsync(default!, TestContext.Current.CancellationToken);

        await sut.UpdateSettingAsync(PipelineRunToEndSetting.Key, "true", TestContext.Current.CancellationToken);
        await repo.Received(1).AddSettingAsync(
            Arg.Is<AppSetting>(setting => setting.Key == PipelineRunToEndSetting.Key && setting.Value == "true"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"runToEnd":true,"idempotencyKey":"k"}""", true)]
    [InlineData("""{"idempotencyKey":"k"}""", false)]
    [InlineData("""{"mode":"prod","AETHEUS_RUN_TO_END":"true"}""", true)]
    [InlineData("""{"mode":"prod"}""", false)]
    public void Request_ReadsTheModeFromBothWireShapes(string json, bool expected)
    {
        var request = JsonSerializer.Deserialize<PipelineRunRequest>(json)!;

        Assert.Equal(expected, request.RunToEnd);
        // The legacy reserved key never reaches the pipeline as a parameter.
        Assert.False(request.Parameters?.ContainsKey("AETHEUS_RUN_TO_END") ?? false);
    }

    [Fact]
    public void Request_AParameterNamedRunToEnd_StaysAParameter()
    {
        var request = JsonSerializer.Deserialize<PipelineRunRequest>("""{"runToEnd":"x"}""")!;

        Assert.False(request.RunToEnd);
        Assert.Equal("x", request.Parameters!["runToEnd"]);
    }

    [Fact]
    public void Request_RoundTripsTheMode()
    {
        var json = JsonSerializer.Serialize(new PipelineRunRequest { RunToEnd = true, IdempotencyKey = "k" });

        Assert.True(JsonSerializer.Deserialize<PipelineRunRequest>(json)!.RunToEnd);
    }

    [Fact]
    public async Task CreateRelease_FromARunToEndRunPastAFailure_IsRefused()
    {
        var releases = Substitute.For<IReleaseService>();
        var runs = Substitute.For<IPipelineRunService>();
        runs.IsServerAssignedToRunAsync(42, 7, Arg.Any<CancellationToken>()).Returns(true);
        runs.GetRunPipelineContextAsync(42, Arg.Any<CancellationToken>()).Returns((5, 3));
        runs.HasRunToEndFailureAsync(42, Arg.Any<CancellationToken>()).Returns(true);
        var sut = new ReleasesController(releases, _authz, Substitute.For<IConfiguration>(), runs,
            Substitute.For<IReleaseDeploymentAnnouncer>(), Substitute.For<IReleaseStatusRestoreService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("ServerId", "7")], "agent"))
                }
            }
        };

        var result = await sut.CreateRelease(
            new CreateReleaseRequest { PipelineRunId = 42, Version = "1.2.3" }, 3, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("run-to-end", Assert.IsType<ApiError>(refused.Value).Message, StringComparison.Ordinal);
        await releases.DidNotReceiveWithAnyArgs().CreateReleaseFromPipelineAsync(
            default, default, default!, default, default, default, default, default, default, TestContext.Current.CancellationToken);
    }
}
