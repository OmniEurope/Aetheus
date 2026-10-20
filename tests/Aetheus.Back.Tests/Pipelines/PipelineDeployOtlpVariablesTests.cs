// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>
/// The zero-config telemetry of a deployed app: when app monitoring has an ingest URL, the deploy task
/// carries the project's OpenTelemetry environment under <c>AETHEUS_DEPLOY_APPENV_</c>; without an ingest
/// URL, without a project, or when the environment cannot be built, the deploy goes ahead without it.
/// </summary>
public class PipelineDeployOtlpVariablesTests
{
    private const int RunId = 41;
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IAppDeployEnvProvider _deployEnv = Substitute.For<IAppDeployEnvProvider>();
    private readonly ILogger<PipelineDeploymentTaskFactory> _logger = Substitute.For<ILogger<PipelineDeploymentTaskFactory>>();
    private readonly Pipeline _pipeline = new() { Id = 3, Name = "deploy" };

    private PipelineDeploymentTaskFactory Factory(string? ingestBaseUrl) =>
        new(_repo, Substitute.For<IArtifactRepository>(), Substitute.For<IEncryptionService>(), _deployEnv,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["AppMonitoring:IngestBaseUrl"] = ingestBaseUrl })
                .Build(),
            _logger, TimeProvider.System);

    private void RunOfProject(int? projectId)
    {
        _repo.GetPipelineRunWithPipelineAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = RunId, PipelineId = _pipeline.Id, Pipeline = _pipeline });
        _repo.GetPipelineProjectIdAsync(_pipeline, Arg.Any<CancellationToken>()).Returns(projectId);
    }

    [Fact]
    public async Task WithAnIngestUrlAndAProject_TheAppEnvironmentIsAddedUnderTheDeployPrefix()
    {
        RunOfProject(9);
        _deployEnv.GetDeployEnvAsync(9, null, RunId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://ingest.local/otlp",
                ["OTEL_SERVICE_NAME"] = "shop",
            });
        var variables = new Dictionary<string, string> { ["AETHEUS_DEPLOY_KIND"] = "binary" };

        await Factory("https://ingest.local").AddDeployOtlpVariablesAsync(RunId, variables, TestContext.Current.CancellationToken);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["AETHEUS_DEPLOY_KIND"] = "binary",
                ["AETHEUS_DEPLOY_APPENV_OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://ingest.local/otlp",
                ["AETHEUS_DEPLOY_APPENV_OTEL_SERVICE_NAME"] = "shop",
            },
            variables);
    }

    [Fact]
    public async Task WithoutAnIngestUrl_NothingIsLookedUp()
    {
        var variables = new Dictionary<string, string>();

        await Factory(ingestBaseUrl: " ").AddDeployOtlpVariablesAsync(RunId, variables, TestContext.Current.CancellationToken);

        Assert.Empty(variables);
        await _repo.DidNotReceive().GetPipelineRunWithPipelineAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARunWithoutAProject_GetsNoAppEnvironment()
    {
        RunOfProject(projectId: null);
        var variables = new Dictionary<string, string>();

        await Factory("https://ingest.local").AddDeployOtlpVariablesAsync(RunId, variables, TestContext.Current.CancellationToken);

        Assert.Empty(variables);
        await _deployEnv.DidNotReceive().GetDeployEnvAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnEnvironmentThatCannotBeBuilt_IsLoggedAndTheDeployGoesAheadWithoutIt()
    {
        RunOfProject(9);
        _deployEnv.GetDeployEnvAsync(9, null, RunId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ingest key store unavailable"));
        var variables = new Dictionary<string, string>();

        await Factory("https://ingest.local").AddDeployOtlpVariablesAsync(RunId, variables, TestContext.Current.CancellationToken);

        Assert.Empty(variables);
        Assert.Contains(_logger.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == "Log"
            && Equals(call.GetArguments()[0], LogLevel.Warning)
            && call.GetArguments()[3] is InvalidOperationException);
    }
}
