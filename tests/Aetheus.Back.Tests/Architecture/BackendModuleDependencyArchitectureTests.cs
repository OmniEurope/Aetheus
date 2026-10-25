// SPDX-License-Identifier: EUPL-1.2

using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Architecture;

public sealed class BackendModuleDependencyArchitectureTests
{
    [Fact]
    public void TasksModule_DoesNotDependOnPipelinesModule()
    {
        var root = FindRepoRoot();
        var tasksDirectory = Path.Combine(root, "src", "Aetheus.Back", "Components", "Tasks");
        var violations = RepositoryScan.Enumerate(tasksDirectory, "*.cs")
            .Where(path => File.ReadAllText(path).Contains(
                "Aetheus.Back.Components.Pipelines",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void ProjectsModule_DoesNotDependOnAnalysisImplementation()
    {
        var root = FindRepoRoot();
        var projectsDirectory = Path.Combine(root, "src", "Aetheus.Back", "Components", "Projects");
        var violations = RepositoryScan.Enumerate(projectsDirectory, "*.cs")
            .Where(path =>
            {
                // Projects reads grades through the IProjectAnalysisGradeReader port only. The port lives in
                // Analysis since 2026-09-25 (layer guard: Analysis L7 must not reach up into Projects L8 to
                // implement it), so the namespace is allowed; its implementations are not.
                var source = File.ReadAllText(path);
                return source.Contains("AnalysisProjectSummaryRepository", StringComparison.Ordinal)
                    || source.Contains("ProjectAnalysisGradeRepository", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void PipelineRepository_ComposesLineageThroughRegisteredPort()
    {
        var root = FindRepoRoot();
        var facade = File.ReadAllText(Path.Combine(
            root, "src", "Aetheus.Back", "Components", "Pipelines", "PipelineRepository.cs"));
        var module = File.ReadAllText(Path.Combine(
            root, "src", "Aetheus.Back", "Components", "Pipelines", "PipelinesModuleExtensions.cs"));

        Assert.DoesNotContain("new PipelineRunLineageRepository", facade, StringComparison.Ordinal);
        Assert.Contains("IPipelineRunLineageReader runLineage", facade, StringComparison.Ordinal);
        Assert.Contains(
            "AddScoped<IPipelineRunLineageReader, PipelineRunLineageRepository>()",
            module,
            StringComparison.Ordinal);
    }

    /// <summary>AUD-07: no SignalR hub takes the 157-method <see cref="Aetheus.Back.Components.Pipelines.IPipelineRepository"/>
    /// contract; PipelineHub resolves run ownership through the narrow <c>IPipelineRunLookupReader</c> port,
    /// which DI must actually provide. Read from the public constructors of the compiled hubs.</summary>
    [Fact]
    public void Hubs_DoNotDependOnThePipelineRepositoryContract()
    {
        var hubs = typeof(Program).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(Microsoft.AspNetCore.SignalR.Hub)))
            .ToList();
        Assert.Contains(typeof(Aetheus.Back.Hubs.PipelineHub), hubs);

        var offenders = hubs
            .SelectMany(hub => hub.GetConstructors().SelectMany(ctor => ctor.GetParameters())
                .Where(parameter => parameter.ParameterType == typeof(Aetheus.Back.Components.Pipelines.IPipelineRepository))
                .Select(_ => hub.Name))
            .ToList();
        Assert.Empty(offenders);

        var pipelineHubParameters = typeof(Aetheus.Back.Hubs.PipelineHub).GetConstructors()
            .SelectMany(ctor => ctor.GetParameters()).Select(parameter => parameter.ParameterType).ToList();
        Assert.Contains(typeof(Aetheus.Back.Services.IPipelineRunLookupReader), pipelineHubParameters);

        var module = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Aetheus.Back", "Components", "Pipelines", "PipelinesModuleExtensions.cs"));
        Assert.Contains(
            "AddScoped<IPipelineRunLookupReader, PipelineRunLookupRepository>()", module, StringComparison.Ordinal);
    }

    /// <summary>AUD-07, suite: each port <see cref="Aetheus.Back.Components.Pipelines.IPipelineRepository"/>
    /// composes is registered on its own repository, never resolved back through the facade, and the
    /// consumers narrowed to one responsibility do not take the facade again. The facade still exposing a
    /// port's members is what lets a consumer slide back unnoticed; this is the ratchet.</summary>
    [Fact]
    public void PipelineRepositoryPorts_AreRegisteredOnTheirOwnAndTheirConsumersStayNarrowed()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddPipelinesModule();
        var ports = typeof(IPipelineRepository).GetInterfaces()
            .Where(port => port != typeof(IPipelineUnitOfWork))
            .ToList();
        Assert.NotEmpty(ports);
        foreach (var port in ports)
        {
            var registration = Assert.Single(services, descriptor => descriptor.ServiceType == port);
            Assert.NotNull(registration.ImplementationType);
            Assert.NotEqual(typeof(PipelineRepository), registration.ImplementationType);
        }

        var narrowedConsumers = new Dictionary<Type, Type[]>
        {
            [typeof(IPipelineTemplateRepository)] =
                [typeof(PipelineTemplateService), typeof(PipelineTemplateResolver), typeof(PipelineFleetService)],
            [typeof(IPipelineRunnerTargetReader)] =
                [typeof(PipelineDispatchServerResolver), typeof(PipelineReadyStageServerSelector),
                    typeof(PipelineScannerManifestPreflight)],
            [typeof(IPipelineAutoTriggerRepository)] =
                [typeof(PipelineWebhookService), typeof(GitPushPipelineTriggerHandler)],
            [typeof(IPipelineApprovalRepository)] = [typeof(PipelineApprovalService)],
            [typeof(IPipelineQualityResultRepository)] =
                [typeof(PipelineArtifactService), typeof(PipelineStepTaskMetricsHandler)],
            [typeof(IPipelineArtifactRepository)] = [typeof(PipelineArtifactService)],
            [typeof(IPipelineRunLookupReader)] = [typeof(PipelineApprovalService), typeof(PipelineArtifactService)],
            [typeof(IPipelineEnvironmentReader)] =
                [typeof(PipelineEnvironmentLockGate), typeof(PipelineEnvironmentCheckGuard)],
            [typeof(IPipelineProjectOwnerReader)] = [typeof(PipelineOwnershipResolver)],
        };
        var offenders = new List<string>();
        foreach (var (port, consumers) in narrowedConsumers)
        {
            foreach (var consumer in consumers)
            {
                var parameters = consumer.GetConstructors()
                    .SelectMany(ctor => ctor.GetParameters()).Select(parameter => parameter.ParameterType).ToList();
                if (parameters.Contains(typeof(IPipelineRepository)) || !parameters.Contains(port))
                    offenders.Add($"{consumer.Name} -> {port.Name}");
            }
        }

        // The scheduler resolves its repository per pass from a scope, not through its constructor.
        var scheduler = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Aetheus.Back", "Components", "Pipelines", "PipelineSchedulerService.cs"));
        if (scheduler.Contains("GetRequiredService<IPipelineRepository>", StringComparison.Ordinal)
            || !scheduler.Contains("GetRequiredService<IPipelineAutoTriggerRepository>", StringComparison.Ordinal))
            offenders.Add("PipelineSchedulerService -> IPipelineAutoTriggerRepository");

        // The release numbers are a static helper: its repository is a method parameter.
        var releaseNumberParameters = typeof(PipelineReleaseNumbers)
            .GetMethod("ResolveAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetParameters().Select(parameter => parameter.ParameterType).ToList();
        if (releaseNumberParameters.Contains(typeof(IPipelineRepository))
            || !releaseNumberParameters.Contains(typeof(IPipelineReleaseNumberRepository)))
            offenders.Add("PipelineReleaseNumbers -> IPipelineReleaseNumberRepository");

        // The retention service, like the scheduler, resolves its repository per pass from a scope.
        var retention = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Aetheus.Back", "Components", "Pipelines", "PipelineRetentionService.cs"));
        if (retention.Contains("GetRequiredService<IPipelineRepository>", StringComparison.Ordinal)
            || !retention.Contains("GetRequiredService<IPipelineRunRetentionRepository>", StringComparison.Ordinal))
            offenders.Add("PipelineRetentionService -> IPipelineRunRetentionRepository");

        Assert.Empty(offenders);
    }

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;
}
