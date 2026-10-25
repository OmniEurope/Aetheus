// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Aetheus.Back.Components.AppBackups;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The cross-agent deploy step.</summary>
public interface IPipelineDeploymentTaskFactory
{
    Task CreateDeployTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, string? stageEnvironment, CancellationToken ct);
}

/// <summary>
/// Builds the cross-agent deploy task: resolve the artifact (same-run by name, or an existing
/// release), build the AETHEUS_DEPLOY_* environment, and dispatch it to the already deploy-vetted
/// server. Any input it cannot resolve fails the step honestly - status Failed plus a run warning -
/// rather than dispatching something that would report a green it did not earn.
/// </summary>
public sealed class PipelineDeploymentTaskFactory(
    IPipelineRepository repo,
    IArtifactRepository artifactRepo,
    IEncryptionService encryption,
    Aetheus.Back.Components.AppMonitoring.IAppDeployEnvProvider appDeployEnv,
    IConfiguration configuration,
    ILogger<PipelineDeploymentTaskFactory> logger,
    TimeProvider timeProvider) : IPipelineDeploymentTaskFactory
{
    // Cross-agent deploy task: resolve the artifact (same-run by name, or an existing release), build
    // the AETHEUS_DEPLOY_* env, and dispatch an OperationKind.PipelineDeploy to the (already
    // deploy-vetted) server. On any resolution failure the step is failed HONESTLY (status Failed +
    // a run warning) - never dispatched as a fake green.
    public async Task CreateDeployTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, string? stageEnvironment, CancellationToken ct)
    {
        var app = SubstituteVariables(stepDef.App ?? string.Empty, legVars).Trim();
        if (!OperationTargetValidator.DeployAppRegex().IsMatch(app))
        {
            await FailDeployStepAsync(
                runId, legServer, stepRun,
                $"invalid or missing 'app' name '{app}' (expected ^[a-zA-Z0-9_-]{{1,64}}$).", ct)
                .ConfigureAwait(false);
            return;
        }
        var source = await ResolveDeploySourceAsync(runId, stepDef, legVars, ct).ConfigureAwait(false);
        if (source.Error is not null)
        {
            await FailDeployStepAsync(runId, legServer, stepRun, source.Error, ct).ConfigureAwait(false);
            return;
        }
        var (deployVars, healthError) = BuildDeployVariables(
            runId, app, source.Artifact!, source.ReleaseId, stepDef, legVars);
        if (healthError is not null)
        {
            await FailDeployStepAsync(runId, legServer, stepRun, healthError, ct).ConfigureAwait(false);
            return;
        }
        await AddDeployOtlpVariablesAsync(runId, stageEnvironment, deployVars, ct).ConfigureAwait(false);
        stepRun.ServerId = legServer.Id;
        var deployTask = new ServerTask
        {
            ServerId = legServer.Id,
            Name = stepRun.StepName,
            Command = app,
            PipelineRunId = runId,
            PipelineStepRunId = stepRun.Id,
            EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(deployVars)),
            TimeoutSeconds = stepDef.TimeoutSeconds,
            Operation = OperationKind.PipelineDeploy
        };
        repo.TrackTask(deployTask);
        ArtifactInputRecorder.Track(repo, runId, stepRun, source.Artifact!, ArtifactInputKind.Deploy,
            source.ReleaseId, timeProvider.GetUtcNow().UtcDateTime);
        MarkStepDispatched(stepRun, deployTask);
    }

    private async Task FailDeployStepAsync(
        int runId, Server server, PipelineStepRun stepRun, string reason, CancellationToken ct)
    {
        stepRun.ServerId = server.Id;
        MarkSystemStepFailed(
            stepRun, TaskFailureCodes.ToolError, reason, timeProvider.GetUtcNow().UtcDateTime);
        await repo.AppendRunWarningsAsync(
            runId, [$"Deploy step '{stepRun.StepName}': {reason}"], ct).ConfigureAwait(false);
    }

    private async Task<DeploySourceResolution> ResolveDeploySourceAsync(
        int runId,
        PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(stepDef.Artifact))
        {
            var artifactName = SubstituteVariables(stepDef.Artifact, legVars).Trim();
            var artifact = await artifactRepo.FindRunArtifactByNameAsync(runId, artifactName, ct).ConfigureAwait(false);
            return artifact is null
                ? DeploySourceResolution.Failed("could not resolve the artifact to deploy (check the artifact name / release selector).")
                : DeploySourceResolution.Resolved(artifact, null);
        }
        if (string.IsNullOrWhiteSpace(stepDef.Release))
            return DeploySourceResolution.Failed(
                "neither 'artifact' (same-run) nor 'release' specified - nothing to deploy.");
        var selector = SubstituteVariables(stepDef.Release, legVars).Trim();
        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        var projectId = run?.Pipeline is null
            ? null
            : await repo.GetPipelineProjectIdAsync(run.Pipeline, ct).ConfigureAwait(false);
        var selection = projectId is { } id
            ? await artifactRepo.FindReleaseArtifactSelectionAsync(id, selector, ct: ct).ConfigureAwait(false)
            : null;
        return selection?.Artifact is null
            ? DeploySourceResolution.Failed("could not resolve the artifact to deploy (check the artifact name / release selector).")
            : DeploySourceResolution.Resolved(selection.Artifact, selection.ReleaseId);
    }

    private static (Dictionary<string, string> Variables, string? Error) BuildDeployVariables(
        int runId,
        string app,
        PipelineArtifact artifact,
        int? releaseId,
        PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars)
    {
        var variables = new Dictionary<string, string>(legVars, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_DEPLOY_ARTIFACT_ID"] = artifact.Id.ToString(),
            ["AETHEUS_DEPLOY_RUN_ID"] = runId.ToString(),
            ["AETHEUS_DEPLOY_APP"] = app,
            ["AETHEUS_DEPLOY_KIND"] = string.IsNullOrWhiteSpace(stepDef.Compose) ? "binary" : "container"
        };
        if (releaseId is { } selectedReleaseId)
            variables["AETHEUS_DEPLOY_RELEASE_ID"] = selectedReleaseId.ToString();
        if (!string.IsNullOrWhiteSpace(stepDef.Compose))
            variables["AETHEUS_DEPLOY_COMPOSE"] = SubstituteVariables(stepDef.Compose, legVars).Trim();
        if (stepDef.HealthTimeoutSeconds > 0)
            variables["AETHEUS_DEPLOY_HEALTH_TIMEOUT"] = stepDef.HealthTimeoutSeconds.ToString();
        if (string.IsNullOrWhiteSpace(stepDef.HealthUrl)) return (variables, null);
        var candidate = SubstituteVariables(stepDef.HealthUrl, legVars).Trim();
        if (!DeployHealthUrlValidator.TryNormalize(candidate, out var healthUrl))
            return (variables, "'health_url' must be an absolute loopback HTTP(S) URL without credentials or fragment.");
        variables["AETHEUS_DEPLOY_HEALTH_URL"] = healthUrl;
        return (variables, null);
    }

    internal async Task AddDeployOtlpVariablesAsync(
        int runId, string? stageEnvironment, IDictionary<string, string> deployVars, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configuration["AppMonitoring:IngestBaseUrl"])) return;
        try
        {
            var target = await DeployObservabilityTarget.ResolveAsync(repo, runId, stageEnvironment, ct).ConfigureAwait(false);
            if (target is null) return;
            var otelEnv = await appDeployEnv.GetDeployEnvAsync(
                target.Value.ProjectId, target.Value.EnvironmentId, runId, ct).ConfigureAwait(false);
            foreach (var (key, value) in otelEnv)
                deployVars[$"AETHEUS_DEPLOY_APPENV_{key}"] = value;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OTLP deploy env injection failed for run {RunId}; deploying without it", runId);
        }
    }

    private sealed record DeploySourceResolution(
        PipelineArtifact? Artifact, int? ReleaseId, string? Error)
    {
        public static DeploySourceResolution Resolved(PipelineArtifact artifact, int? releaseId) =>
            new(artifact, releaseId, null);
        public static DeploySourceResolution Failed(string error) => new(null, null, error);
    }
}
