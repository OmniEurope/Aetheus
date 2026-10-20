// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks.Events;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>AUD-07, suite: the real container resolves each port extracted from IPipelineRepository on
/// its own repository and builds every consumer narrowed to it, so the narrowing is wired in DI and not
/// only in a unit test's hand-made constructor call.</summary>
public sealed class PipelineRepositoryPortsCompositionTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public void TheApplicationContainer_ResolvesEachPortOnItsOwnRepository()
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.IsType<PipelineTemplateRepository>(services.GetRequiredService<IPipelineTemplateRepository>());
        Assert.IsType<PipelineServerResolver>(services.GetRequiredService<IPipelineRunnerTargetReader>());
        Assert.IsType<PipelineAutoTriggerRepository>(services.GetRequiredService<IPipelineAutoTriggerRepository>());
        Assert.IsType<PipelineApprovalRepository>(services.GetRequiredService<IPipelineApprovalRepository>());
        Assert.IsType<PipelineCoverageRepository>(services.GetRequiredService<IPipelineQualityResultRepository>());
        Assert.IsType<PipelineArtifactRepository>(services.GetRequiredService<IPipelineArtifactRepository>());
        Assert.IsType<PipelineRunLookupRepository>(services.GetRequiredService<IPipelineRunLookupReader>());
        Assert.IsType<PipelineEnvironmentRepository>(services.GetRequiredService<IPipelineEnvironmentReader>());
        Assert.IsType<ProjectReleaseNumberRepository>(services.GetRequiredService<IPipelineReleaseNumberRepository>());
        Assert.IsType<PipelineRunRetentionRepository>(services.GetRequiredService<IPipelineRunRetentionRepository>());
        Assert.IsType<PipelineProjectOwnerRepository>(services.GetRequiredService<IPipelineProjectOwnerReader>());
    }

    [Fact]
    public void TheApplicationContainer_BuildsTheNarrowedConsumers()
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.IsType<PipelineTemplateService>(services.GetRequiredService<IPipelineTemplateService>());
        Assert.IsType<PipelineTemplateResolver>(services.GetRequiredService<IPipelineTemplateResolver>());
        Assert.IsType<PipelineFleetService>(services.GetRequiredService<IPipelineFleetService>());
        Assert.IsType<PipelineDispatchServerResolver>(services.GetRequiredService<IPipelineDispatchServerResolver>());
        Assert.IsType<PipelineScannerManifestPreflight>(services.GetRequiredService<IPipelineScannerManifestPreflight>());
        Assert.IsType<PipelineWebhookService>(services.GetRequiredService<IPipelineWebhookService>());
        Assert.Contains(
            services.GetServices<IDomainEventHandler<GitPushProcessedEvent>>(),
            handler => handler is GitPushPipelineTriggerHandler);
        Assert.IsType<PipelineApprovalService>(services.GetRequiredService<IPipelineApprovalService>());
        Assert.IsType<PipelineArtifactService>(services.GetRequiredService<IPipelineArtifactService>());
        Assert.Contains(
            services.GetServices<IDomainEventHandler<PipelineStepTaskMetricsCollectedEvent>>(),
            handler => handler is PipelineStepTaskMetricsHandler);
        Assert.IsType<PipelineEnvironmentCheckGuard>(services.GetRequiredService<IPipelineEnvironmentCheckGuard>());
    }

    [Fact]
    public async Task TheScheduler_ReadsItsScheduledPipelinesThroughTheTriggerPort()
    {
        var scheduler = ActivatorUtilities.CreateInstance<PipelineSchedulerService>(factory.Services);

        // A pass resolves IPipelineAutoTriggerRepository from a fresh scope; a missing registration throws.
        await scheduler.CheckScheduledPipelinesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheRetentionService_DeletesItsRunsThroughTheRetentionPort()
    {
        var retention = ActivatorUtilities.CreateInstance<PipelineRetentionService>(factory.Services);

        // A pass resolves IPipelineRunRetentionRepository from a fresh scope; a missing registration throws.
        await retention.PruneOldRunsAsync(TestContext.Current.CancellationToken);
    }
}
