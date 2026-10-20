// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// What a step dispatch did: the task was handled, or the attempt finalized the whole run because the
/// step definition could not be honoured. Top-level so the step factories can return it.
/// </summary>
public enum StepDispatchResult
{
    Handled,
    RunFinalized
}

/// <summary>The security-scanner step: manifest compatibility, DAST dispatch policy, execution lease.</summary>
public interface IPipelineScannerTaskFactory
{
    Task<StepDispatchResult> CreateScannerTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef, bool stageIsContainer,
        CancellationToken ct);
}

/// <summary>
/// Dispatches a scanner to an agent, but only to one that reports the exact scanner manifest this
/// backend knows: a drifted or missing manifest fails the step rather than running a scan whose
/// results could not be trusted. A DAST scan additionally needs a dispatch policy to allow it and
/// carries a bounded execution lease, so the target and the window are both explicit.
/// </summary>
public sealed class PipelineScannerTaskFactory(
    IPipelineRepository repo,
    IEncryptionService encryption,
    TimeProvider timeProvider) : IPipelineScannerTaskFactory
{
    public async Task<StepDispatchResult> CreateScannerTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, PipelineStageDefinition stageDef, bool stageIsContainer,
        CancellationToken ct)
    {
        var scanner = string.IsNullOrWhiteSpace(stepDef.Scanner)
            ? null
            : ScannerManifestCatalog.Find(stepDef.Scanner);
        if (scanner is null)
        {
            await FailScannerStepAsync(
                runId, stepRun, "scanner key is absent from scanner-manifest.json.", ct).ConfigureAwait(false);
            return StepDispatchResult.Handled;
        }
        if (!HasCompatibleScannerManifest(legServer, legVars))
        {
            await FailScannerStepAsync(
                runId,
                stepRun,
                $"candidate scanner preflight requires agent manifest {ScannerManifestCatalog.Sha256}; "
                + $"agent '{legServer.Name}' reported "
                + $"{TaskRepository.ExtractScannerManifestSha256(legServer.ScannerCapabilitiesJson) ?? "no manifest hash"}. "
                + "Update the agent from its server page (Update agent).",
                ct).ConfigureAwait(false);
            return StepDispatchResult.Handled;
        }

        DastDispatchPolicy? dastPolicy = null;
        var resolvedStep = stepDef with
        {
            TargetUrl = SubstituteVariables(stepDef.TargetUrl ?? string.Empty, legVars),
            ApiSpecificationUrl = SubstituteVariables(stepDef.ApiSpecificationUrl ?? string.Empty, legVars),
            ApiSpecificationUrls = [.. stepDef.ApiSpecificationUrls.Select(url => SubstituteVariables(url, legVars).Trim())],
            ApiSpecificationFormat = SubstituteVariables(stepDef.ApiSpecificationFormat ?? string.Empty, legVars)
        };
        if (scanner.Key.StartsWith("zap-", StringComparison.OrdinalIgnoreCase))
        {
            dastPolicy = await ResolveDastDispatchPolicyAsync(
                runId, stageDef, resolvedStep, scanner, ct).ConfigureAwait(false);
            if (!dastPolicy.Allowed)
            {
                await FailScannerStepAsync(
                    runId, stepRun, dastPolicy.Error ?? "DAST dispatch policy refused the scan.", ct)
                    .ConfigureAwait(false);
                return StepDispatchResult.Handled;
            }
        }

        var scannerVars = CreateScannerVariables(
            runId, legVars, stageDef, stepDef, resolvedStep, dastPolicy, stageIsContainer);
        if (dastPolicy?.Allowed == true)
            AddDastExecutionLease(runId, stepRun, stepDef, dastPolicy, scannerVars);
        stepRun.ServerId = legServer.Id;
        var task = new ServerTask
        {
            ServerId = legServer.Id,
            Name = stepRun.StepName,
            Command = scanner.Key,
            PipelineRunId = runId,
            PipelineStepRunId = stepRun.Id,
            EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(scannerVars)),
            TimeoutSeconds = stepDef.TimeoutSeconds,
            Operation = OperationKind.PipelineRunScanner
        };
        repo.TrackTask(task);
        MarkStepDispatched(stepRun, task);
        return StepDispatchResult.Handled;
    }

    internal static bool HasCompatibleScannerManifest(
        Server server,
        IReadOnlyDictionary<string, string> variables)
    {
        if (!RequiresCandidateScannerManifest(
                variables.GetValueOrDefault("UPSTREAM_PIPELINE"),
                variables.GetValueOrDefault(UpstreamTemplateVariable)))
            return true;
        return ServerDataMapper.DeserializeDiagnostics(server.ScannerCapabilitiesJson).Contains(
            $"scanner-manifest:sha256:{ScannerManifestCatalog.Sha256}",
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The shipped template the parent of a triggered run extends, written by the engine beside
    /// <c>UPSTREAM_PIPELINE</c> (never by a definition: the engine overwrites it).
    /// </summary>
    internal const string UpstreamTemplateVariable = "UPSTREAM_PIPELINE_TEMPLATE";

    /// <summary>Candidate-triggered scanners require the backend's exact embedded manifest. The
    /// shared preflight rule recognizes the self-deploy candidate and pipelines extending the
    /// shipped candidate template.</summary>
    internal static bool RequiresCandidateScannerManifest(string? upstreamPipeline, string? upstreamTemplate) =>
        string.Equals(upstreamPipeline, "aetheus-candidate", StringComparison.OrdinalIgnoreCase)
        || DeliveryTemplateNames.Is(upstreamTemplate, DeliveryTemplateNames.Candidate);

    private async Task FailScannerStepAsync(
        int runId, PipelineStepRun stepRun, string reason, CancellationToken ct)
    {
        MarkSystemStepFailed(
            stepRun, TaskFailureCodes.ToolError, reason, timeProvider.GetUtcNow().UtcDateTime);
        await repo.AppendRunWarningsAsync(
            runId, [$"Scanner step '{stepRun.StepName}': {reason}"], ct).ConfigureAwait(false);
    }

    private static Dictionary<string, string> CreateScannerVariables(
        int runId,
        Dictionary<string, string> legVars,
        PipelineStageDefinition stageDef,
        PipelineStepDefinition stepDef,
        PipelineStepDefinition resolvedStep,
        DastDispatchPolicy? dastPolicy,
        bool stageIsContainer) =>
        new(legVars, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_RUN_ID"] = runId.ToString(),
            ["AETHEUS_STAGE_NAME"] = stageDef.Name,
            ["AETHEUS_STEP_NAME"] = stepDef.Name,
            ["AETHEUS_WORKING_DIR"] = legVars.GetValueOrDefault("WORKSPACE", ""),
            ["AETHEUS_WORKSPACE_MODE"] = stageIsContainer ? "container" : "process",
            ["AETHEUS_SCANNER_TARGET_URL"] = resolvedStep.TargetUrl ?? string.Empty,
            ["AETHEUS_SCANNER_TARGET_CLASSIFICATION"] = dastPolicy?.Classification ?? string.Empty,
            ["AETHEUS_SCANNER_TARGET_ALLOWED_HOST"] = dastPolicy?.AllowedHost ?? string.Empty,
            ["AETHEUS_SCANNER_TARGET_TRUSTED"] = dastPolicy?.Allowed == true ? "true" : "false",
            ["AETHEUS_SCANNER_ENVIRONMENT_ID"] = dastPolicy?.EnvironmentId.ToString() ?? string.Empty,
            ["AETHEUS_SCANNER_ENVIRONMENT_NAME"] = stageDef.Environment ?? string.Empty,
            ["AETHEUS_SCANNER_ACTIVE"] = stepDef.Active ? "true" : "false",
            ["AETHEUS_SCANNER_API_SPECIFICATION_URL"] = resolvedStep.ApiSpecificationUrl ?? string.Empty,
            // The list is scanned sequentially; leaving the single URL empty makes agents without
            // list support refuse instead of scanning only part.
            ["AETHEUS_SCANNER_API_SPECIFICATION_URLS"] = string.Join('\n', resolvedStep.ApiSpecificationUrls),
            ["AETHEUS_SCANNER_API_SPECIFICATION_FORMAT"] = resolvedStep.ApiSpecificationFormat ?? string.Empty
        };

    private void AddDastExecutionLease(
        int runId,
        PipelineStepRun stepRun,
        PipelineStepDefinition stepDef,
        DastDispatchPolicy policy,
        IDictionary<string, string> scannerVars)
    {
        var leaseToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var leaseLifetime = TimeSpan.FromSeconds(Math.Clamp(stepDef.TimeoutSeconds + 900, 900, 86_400));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var leaseExpiresAt = now + leaseLifetime;
        repo.TrackDastExecutionLease(new DastExecutionLease
        {
            PipelineRunId = runId,
            PipelineStepRunId = stepRun.Id,
            PipelineStepRun = stepRun,
            Token = leaseToken,
            EnvironmentId = policy.EnvironmentId,
            TargetHost = policy.AllowedHost,
            TargetPort = policy.TargetPort,
            ExpiresAt = leaseExpiresAt,
            CreatedAt = now
        });
        scannerVars["AETHEUS_SCANNER_DAST_LEASE_TOKEN"] = leaseToken;
        scannerVars["AETHEUS_SCANNER_DAST_LEASE_EXPIRES_AT"] = leaseExpiresAt.ToString("O");
    }



    private async Task<DastDispatchPolicy> ResolveDastDispatchPolicyAsync(
        int runId,
        PipelineStageDefinition stage,
        PipelineStepDefinition step,
        ScannerManifestEntry scanner,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stage.Environment))
            return DastDispatchPolicy.Deny("DAST requires a named Aetheus environment.");
        var (environment, environmentError) = await ResolveDastEnvironmentAsync(
            runId, stage.Environment, ct).ConfigureAwait(false);
        if (environmentError is not null) return DastDispatchPolicy.Deny(environmentError);
        var (target, targetError) = ValidateDastTarget(environment!, step.TargetUrl);
        if (targetError is not null) return DastDispatchPolicy.Deny(targetError);
        var modeError = ValidateDastScannerMode(scanner, step, target!);
        if (modeError is not null) return DastDispatchPolicy.Deny(modeError);
        return new DastDispatchPolicy(
            true,
            null,
            environment!.Id,
            target!.Host.TrimEnd('.'),
            target.IsDefaultPort ? target.Scheme == Uri.UriSchemeHttp ? 80 : 443 : target.Port,
            "ephemeral");
    }

    private async Task<(Data.Entities.Environment? Environment, string? Error)> ResolveDastEnvironmentAsync(
        int runId,
        string environmentName,
        CancellationToken ct)
    {
        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        var projectId = run?.Pipeline is null
            ? null
            : await repo.GetPipelineProjectIdAsync(run.Pipeline, ct).ConfigureAwait(false);
        if (!projectId.HasValue)
            return (null, "DAST requires a project-owned pipeline.");
        var environment = await repo.FindEnvironmentByNameForProjectAsync(
            environmentName, projectId.Value, ct).ConfigureAwait(false);
        if (environment is null)
            return (null, "the named DAST environment does not belong to this project.");
        if (!environment.DastEnabled || environment.Type == EnvironmentType.Production)
            return (null, "DAST is disabled or the environment is production.");
        if (environment.DastContainsRealData)
            return (null, "DAST is refused because the environment contains real data.");
        if (!IsDastEnvironmentEligible(environment.Type, environment.DastIsEphemeral, environment.DastContainsRealData))
            return (null, "DAST requires an explicitly ephemeral Testing or Staging environment.");
        return (environment, null);
    }

    private static (Uri? Target, string? Error) ValidateDastTarget(
        Data.Entities.Environment environment,
        string? targetUrl)
    {
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var target)
            || target.Scheme is not ("http" or "https"))
            return (null, "the target URL is invalid.");
        var allowedHosts = environment.DastAllowedHosts
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!allowedHosts.Contains(target.Host.TrimEnd('.'), StringComparer.OrdinalIgnoreCase))
            return (null, "the target hostname is not allowlisted on the environment.");
        var isLocalTarget = target.IsLoopback || target.Host.EndsWith(".test", StringComparison.OrdinalIgnoreCase);
        if (target.Scheme != Uri.UriSchemeHttps && !isLocalTarget)
            return (null, "non-local DAST targets require HTTPS.");
        return (target, null);
    }

    private static string? ValidateDastScannerMode(
        ScannerManifestEntry scanner,
        PipelineStepDefinition step,
        Uri target)
    {
        var active = string.Equals(scanner.Key, "zap-active", StringComparison.OrdinalIgnoreCase);
        if (active && !step.Active)
            return "active DAST requires an explicitly ephemeral Testing or Staging environment.";
        if (!active && step.Active)
            return "the active flag requires the zap-active scanner.";
        if (!string.Equals(scanner.Key, "zap-api", StringComparison.OrdinalIgnoreCase)) return null;
        List<string> specifications = step.ApiSpecificationUrls.Count > 0
            ? step.ApiSpecificationUrls
            : [step.ApiSpecificationUrl ?? string.Empty];
        if (specifications.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var specification)
                || !IsDastApiSpecificationEligible(target, specification))
            || step.ApiSpecificationFormat?.ToLowerInvariant() is not ("openapi" or "graphql"))
            return "the API specification must use HTTPS, except on loopback QA, use the target host and declare openapi or graphql.";
        return null;
    }

    internal static bool IsDastEnvironmentEligible(
        EnvironmentType type,
        bool isEphemeral,
        bool containsRealData) =>
        isEphemeral
        && !containsRealData
        && type is EnvironmentType.Testing or EnvironmentType.Staging;

    internal static bool IsDastApiSpecificationEligible(Uri target, Uri specification) =>
        string.Equals(
            specification.Host.TrimEnd('.'),
            target.Host.TrimEnd('.'),
            StringComparison.OrdinalIgnoreCase)
        && (specification.Scheme == Uri.UriSchemeHttps
            || specification.Scheme == Uri.UriSchemeHttp
                && specification.IsLoopback
                && target.IsLoopback);

    private sealed record DastDispatchPolicy(
        bool Allowed,
        string? Error,
        int EnvironmentId,
        string AllowedHost,
        int TargetPort,
        string Classification)
    {
        public static DastDispatchPolicy Deny(string error) =>
            new(false, error, 0, string.Empty, 0, string.Empty);
    }

}
