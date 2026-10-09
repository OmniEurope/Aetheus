// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Aetheus.Back.Components.AppBackups;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The host-operation step types: the Apache proxy and config steps, certbot, the smoke test and the
/// blue-green operations. Every one of them targets a host rather than an application artifact.
/// </summary>
public interface IPipelineHostOperationTaskFactory
{
    Task CreateApacheProxyTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct);

    Task CreateApacheConfigTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct);

    Task CreateCertbotTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct);

    Task CreateSmokeTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct);

    Task CreateBlueGreenTaskAsync(
        string type, int runId, Server legServer, PipelineStepRun stepRun,
        PipelineStepDefinition stepDef, Dictionary<string, string> legVars, CancellationToken ct);
}

/// <summary>
/// Renders and dispatches the host operations, all of them pinned to the run's immutable commit.
/// Same contract as the deploy factory: an input it cannot resolve fails the step honestly instead of
/// dispatching a task that could only report a green it did not earn.
/// </summary>
public sealed class PipelineHostOperationTaskFactory(
    IPipelineRepository repo,
    IPipelineGitService pipelineGit,
    IEncryptionService encryption,
    ISecretMaskingService secretMasking,
    IConfiguration configuration,
    IAppDeployEnvProvider appDeployEnv,
    ILogger<PipelineHostOperationTaskFactory> logger,
    TimeProvider timeProvider,
    IPipelineResourceLockRepository? resourceLocks = null) : IPipelineHostOperationTaskFactory
{

    private async Task FailPipelineOperationStepAsync(
        string operation, int runId, Server server, PipelineStepRun stepRun,
        string reason, CancellationToken ct)
    {
        stepRun.ServerId = server.Id;
        MarkSystemStepFailed(
            stepRun, TaskFailureCodes.ToolError, reason, timeProvider.GetUtcNow().UtcDateTime);
        await repo.AppendRunWarningsAsync(
            runId, [$"{operation} step '{stepRun.StepName}': {reason}"], ct).ConfigureAwait(false);
    }

    private async Task<ImmutableProjectRun?> ResolveImmutableProjectRunAsync(
        int runId, CancellationToken ct)
    {
        var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
        return run?.Pipeline?.ProjectId is { } projectId
            && PipelineRunService.ResolveDefinitionCommit(run) is { } definitionCommit
            && IsGitCommitHash(definitionCommit)
            ? new ImmutableProjectRun(run, projectId, definitionCommit)
            : null;
    }

    /// <param name="DefinitionCommit">The revision the versioned templates are read at: the run's own,
    /// or its definition's when the workspace comes from another repository (recette R-534).</param>
    private sealed record ImmutableProjectRun(PipelineRun Run, int ProjectId, string DefinitionCommit);

    // Apache reverse-proxy task: render a versioned .pipeline/configs template at the immutable run
    // commit, base64 it, and dispatch an OperationKind.ApacheConfigureProxy to the Apache-manage
    // server. Invalid/missing inputs fail the step honestly (status Failed + run warning).
    public async Task CreateApacheProxyTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct)
    {
        Task FailAsync(string reason) => FailPipelineOperationStepAsync(
            "Apache-proxy", runId, legServer, stepRun, reason, ct);

        var serverName = SubstituteVariables(stepDef.ServerName ?? string.Empty, legVars).Trim();
        var upstream = SubstituteVariables(stepDef.Upstream ?? string.Empty, legVars).Trim();

        if (!MailValidation.IsValidDomainName(serverName))
        {
            await FailAsync($"invalid or missing 'server_name' '{serverName}'.").ConfigureAwait(false);
            return;
        }
        if (!Uri.TryCreate(upstream, UriKind.Absolute, out var upstreamUri) ||
            (upstreamUri.Scheme != Uri.UriSchemeHttp && upstreamUri.Scheme != Uri.UriSchemeHttps))
        {
            await FailAsync($"invalid or missing 'upstream' '{upstream}' (expected http(s)://host:port).").ConfigureAwait(false);
            return;
        }

        var siteFile = $"{serverName}.conf";
        if (!OperationTargetValidator.ApacheSiteFileRegex().IsMatch(siteFile))
        {
            await FailAsync($"could not derive a safe vhost filename from '{serverName}'.").ConfigureAwait(false);
            return;
        }

        const string DefaultTemplatePath = ".pipeline/configs/apache/reverse-proxy-http.conf";
        var templatePath = string.IsNullOrWhiteSpace(stepDef.ConfigTemplate)
            ? DefaultTemplatePath
            : stepDef.ConfigTemplate.Trim();
        if (!PipelineConfigTemplateRenderer.IsSafeConfigPath(templatePath))
        {
            await FailAsync($"invalid 'config_template' path '{templatePath}'.").ConfigureAwait(false);
            return;
        }

        var source = await ResolveImmutableProjectRunAsync(runId, ct).ConfigureAwait(false);
        if (source is null)
        {
            await FailAsync("the project source repository and immutable commit are required.")
                .ConfigureAwait(false);
            return;
        }

        var template = await pipelineGit.ReadProjectConfigAtRevisionAsync(
            source.ProjectId, templatePath, source.DefinitionCommit, ct,
            source.Run.Pipeline!.SourceRepositoryId).ConfigureAwait(false);
        if (template is null)
        {
            await FailAsync($"template '{templatePath}' was not found at commit {source.DefinitionCommit}.")
                .ConfigureAwait(false);
            return;
        }

        var upstreamBase = upstream.TrimEnd('/');
        var renderVars = new Dictionary<string, string>(legVars, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_APACHE_SERVER_NAME"] = serverName,
            ["AETHEUS_APACHE_UPSTREAM"] = upstreamBase
        };
        var vhost = PipelineConfigTemplateRenderer.RenderStrict(template, renderVars);

        var proxyVars = new Dictionary<string, string>(legVars, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_APACHE_CONFIG_B64"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(vhost)),
            ["AETHEUS_APACHE_SERVERNAME"] = serverName
        };

        stepRun.ServerId = legServer.Id;
        var task = new ServerTask
        {
            ServerId = legServer.Id,
            Name = stepRun.StepName,
            Command = siteFile,
            PipelineRunId = runId,
            PipelineStepRunId = stepRun.Id,
            EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(proxyVars)),
            TimeoutSeconds = stepDef.TimeoutSeconds,
            Operation = OperationKind.ApacheConfigureProxy
        };
        repo.TrackTask(task);
        MarkStepDispatched(stepRun, task);
    }

    public async Task CreateApacheConfigTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct)
    {
        Task FailAsync(string reason) => FailPipelineOperationStepAsync(
            "Apache-config", runId, legServer, stepRun, reason, ct);

        var source = await ResolveImmutableProjectRunAsync(runId, ct).ConfigureAwait(false);
        if (source is null)
        {
            await FailAsync("the project source repository and immutable commit are required.")
                .ConfigureAwait(false);
            return;
        }
        if (stepDef.ConfigFiles.Count == 0 || stepDef.ConfigFiles.Count > 32)
        {
            await FailAsync("config_files must contain between 1 and 32 entries.")
                .ConfigureAwait(false);
            return;
        }

        var renderedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (destinationPattern, templatePath) in stepDef.ConfigFiles)
        {
            var destination = SubstituteVariables(destinationPattern, legVars).Trim();
            if (!OperationTargetValidator.ApacheSiteFileRegex().IsMatch(destination))
            {
                await FailAsync($"invalid destination filename '{destination}'.").ConfigureAwait(false);
                return;
            }

            var template = await pipelineGit.ReadProjectConfigAtRevisionAsync(
                source.ProjectId, templatePath, source.DefinitionCommit, ct,
                source.Run.Pipeline!.SourceRepositoryId).ConfigureAwait(false);
            if (template is null)
            {
                await FailAsync(
                    $"template '{templatePath}' was not found at commit {source.DefinitionCommit}.")
                    .ConfigureAwait(false);
                return;
            }

            var rendered = PipelineConfigTemplateRenderer.RenderStrict(template, legVars);
            renderedFiles[destination] = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(rendered));
        }

        var manifest = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(renderedFiles)));
        var taskVariables = new Dictionary<string, string>(legVars, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_APACHE_CONFIG_SET_B64"] = manifest
        };
        stepRun.ServerId = legServer.Id;
        var task = new ServerTask
        {
            ServerId = legServer.Id,
            Name = stepRun.StepName,
            Command = "config-set",
            PipelineRunId = runId,
            PipelineStepRunId = stepRun.Id,
            EnvironmentVariables = TaskEnvProtection.Protect(
                encryption, JsonSerializer.Serialize(taskVariables)),
            TimeoutSeconds = stepDef.TimeoutSeconds,
            Operation = OperationKind.ApacheApplyConfigSet
        };
        repo.TrackTask(task);
        MarkStepDispatched(stepRun, task);
    }

    // Certbot task: dispatch an OperationKind.CertbotObtain for the requested domain(s). Production
    // mode requires real ACME issuance and fails honestly when validation cannot complete. A self-signed
    // certificate is available only through the explicit local mode. Invalid/missing inputs fail too.
    public async Task CreateCertbotTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct)
    {
        Task FailAsync(string reason) => FailPipelineOperationStepAsync(
            "Certbot", runId, legServer, stepRun, reason, ct);

        var domainsRaw = SubstituteVariables(stepDef.Domains ?? string.Empty, legVars);
        var domains = domainsRaw
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var email = SubstituteVariables(stepDef.Email ?? string.Empty, legVars).Trim();

        if (domains.Count == 0 || domains.Any(d => !MailValidation.IsValidDomainName(d)))
        {
            await FailAsync($"invalid or missing 'domains' '{domainsRaw}'.").ConfigureAwait(false);
            return;
        }
        if (!string.IsNullOrEmpty(email) && !MailValidation.IsValidEmail(email))
        {
            await FailAsync($"invalid 'email' '{email}'.").ConfigureAwait(false);
            return;
        }

        var certVars = new Dictionary<string, string>(legVars, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_CERTBOT_DOMAINS"] = string.Join(",", domains),
            ["AETHEUS_CERTBOT_EMAIL"] = email,
            ["AETHEUS_CERTBOT_MODE"] = legVars.GetValueOrDefault("CERTBOT_MODE", PipelineDeploymentTargetGuard.Production)
        };

        stepRun.ServerId = legServer.Id;
        var task = new ServerTask
        {
            ServerId = legServer.Id,
            Name = stepRun.StepName,
            Command = domains[0],
            PipelineRunId = runId,
            PipelineStepRunId = stepRun.Id,
            EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(certVars)),
            TimeoutSeconds = stepDef.TimeoutSeconds,
            Operation = OperationKind.CertbotObtain
        };
        repo.TrackTask(task);
        MarkStepDispatched(stepRun, task);
    }

    /// <summary>
    /// Resolves <c>evidence_gate:</c>. A typo would silently downgrade a blocking gate to advisory,
    /// which is the exact failure the gate exists to prevent, so an unrecognised value fails the step
    /// instead of defaulting.
    /// </summary>
    private bool TryResolveEvidenceGate(
        PipelineStepDefinition stepDef, Dictionary<string, string> legVars, out string gate, out string error)
    {
        gate = SubstituteVariables(stepDef.EvidenceGate ?? string.Empty, legVars).Trim().ToLowerInvariant();
        if (gate is "" or "advisory" or "blocking")
        {
            error = string.Empty;
            return true;
        }
        error = $"'evidence_gate' must be 'advisory' or 'blocking', got '{gate}'.";
        return false;
    }

    /// <summary>
    /// Validates every smoke tuning value and turns it into the task environment, or returns the
    /// reason it is unusable. Kept apart from task creation so each stays readable: a probe definition
    /// that is merely plausible is not enough when the step grades a live deployment.
    /// </summary>
    /// <summary>
    /// The origin the browser suite authenticates against. Empty means same-origin, which keeps every
    /// existing consumer exactly as it was. When it is set it must still be an origin and nothing
    /// else, for the same reason `origin` is: the agent composes every probe path itself, so a step
    /// cannot aim one at an arbitrary endpoint.
    /// </summary>
    private bool TryResolveApiOrigin(
        PipelineStepDefinition stepDef, Dictionary<string, string> legVars,
        out string apiOrigin, out string error)
    {
        error = string.Empty;
        apiOrigin = SubstituteVariables(stepDef.ApiOrigin ?? string.Empty, legVars).Trim().TrimEnd('/');
        if (apiOrigin.Length == 0) return true;
        if (OperationTargetValidator.IsValid(OperationKind.PipelineSmoke, apiOrigin)) return true;
        error = $"invalid 'api_origin' '{apiOrigin}'.";
        return false;
    }

    private bool TryBuildSmokeVariables(
        PipelineStepDefinition stepDef, Dictionary<string, string> legVars,
        out Dictionary<string, string> smokeVars, out string error)
    {
        smokeVars = [];
        var maxSeconds = SubstituteVariables(stepDef.MaxSeconds ?? string.Empty, legVars).Trim();
        if (maxSeconds.Length > 0
            && (!double.TryParse(maxSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var budget) || budget <= 0))
        {
            error = $"invalid 'max_seconds' '{maxSeconds}'.";
            return false;
        }

        var samplesRaw = SubstituteVariables(stepDef.Samples ?? string.Empty, legVars).Trim();
        var samples = 1;
        if (samplesRaw.Length > 0
            && (!int.TryParse(samplesRaw, NumberStyles.None, CultureInfo.InvariantCulture, out samples)
                || samples is < 1 or > 50))
        {
            error = $"'samples' must be between 1 and 50, got '{samplesRaw}'.";
            return false;
        }

        if (!TryResolveEvidenceGate(stepDef, legVars, out var gate, out error)) return false;

        if (!TryResolveApiOrigin(stepDef, legVars, out var apiOrigin, out error)) return false;

        smokeVars = new Dictionary<string, string>(legVars, StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_SMOKE_API_ORIGIN"] = apiOrigin,
            ["AETHEUS_SMOKE_SAMPLES"] = samples.ToString(CultureInfo.InvariantCulture),
            ["AETHEUS_SMOKE_MAX_SECONDS"] = maxSeconds,
            ["AETHEUS_SMOKE_FRONTEND"] = stepDef.Frontend ? "true" : "false",
            ["AETHEUS_SMOKE_BROWSER_IMAGE"] = SubstituteVariables(stepDef.BrowserImage ?? string.Empty, legVars).Trim(),
            ["AETHEUS_SMOKE_FILTER"] = SubstituteVariables(stepDef.Filter ?? string.Empty, legVars).Trim(),
            ["AETHEUS_SMOKE_GATE"] = gate
        };
        error = string.Empty;
        return true;
    }

    // type: smoke - probe an already-deployed origin and record findings. The executor always publishes
    // SMOKE_GATE_STATUS / SMOKE_FINDINGS / SMOKE_BLOCKING_FINDINGS; `evidence_gate:` decides what a
    // finding costs. advisory (default) exits 0 as before; blocking fails the step on a blocking
    // finding, so a failed() compensation stage can actually fire. Only an unusable definition fails here.
    public async Task CreateSmokeTaskAsync(
        int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct)
    {
        Task FailAsync(string reason) => FailPipelineOperationStepAsync(
            "Smoke", runId, legServer, stepRun, reason, ct);

        var origin = SubstituteVariables(stepDef.Origin ?? string.Empty, legVars).Trim().TrimEnd('/');
        if (!OperationTargetValidator.IsValid(OperationKind.PipelineSmoke, origin))
        {
            await FailAsync($"invalid or missing 'origin' '{origin}'.").ConfigureAwait(false);
            return;
        }
        if (!TryAddDeploymentBootstrapIdentity(runId, legVars, forSmoke: true, out var identityError))
        {
            await FailAsync(identityError).ConfigureAwait(false);
            return;
        }
        if (!TryBuildSmokeVariables(stepDef, legVars, out var smokeVars, out var definitionError))
        {
            await FailAsync(definitionError).ConfigureAwait(false);
            return;
        }

        stepRun.ServerId = legServer.Id;
        var task = new ServerTask
        {
            ServerId = legServer.Id,
            Name = stepRun.StepName,
            Command = origin,
            PipelineRunId = runId,
            PipelineStepRunId = stepRun.Id,
            EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(smokeVars)),
            TimeoutSeconds = stepDef.TimeoutSeconds,
            Operation = OperationKind.PipelineSmoke
        };
        repo.TrackTask(task);
        MarkStepDispatched(stepRun, task);
    }

    // type: bluegreen-* - one blue-green host deployment step. The four operations share the same
    // environment description because they run as separate steps and each re-derives it rather than
    // inheriting anything from a predecessor's process.
    public async Task CreateBlueGreenTaskAsync(
        string type, int runId, Server legServer, PipelineStepRun stepRun, PipelineStepDefinition stepDef,
        Dictionary<string, string> legVars, CancellationToken ct)
    {
        Task FailAsync(string reason) => FailPipelineOperationStepAsync(
            "Blue-green", runId, legServer, stepRun, reason, ct);

        var operation = BlueGreenStepBinding.OperationFor(type);
        if (operation == OperationKind.None)
        {
            await FailAsync($"unknown blue-green step type '{type}'.").ConfigureAwait(false);
            return;
        }
        // Only the step that starts the candidate colour: the identity has to be in the container
        // environment at start-up, and the later steps act on a colour that already carries it.
        if (operation == OperationKind.BlueGreenUp
            && !TryAddDeploymentBootstrapIdentity(runId, legVars, forSmoke: false, out var identityError))
        {
            await FailAsync(identityError).ConfigureAwait(false);
            return;
        }

        // Same step, same reason: telemetry has to be in the container environment at start-up. The
        // ingest key is derived by the control plane rather than published by a step, because a
        // setvariable directive would echo it into the run log. Without this the blue-green path
        // deployed production with telemetry disabled - the type: deploy path already did it, and
        // nothing carried it over when this pipeline moved to the reusable cutover.
        if (operation == OperationKind.BlueGreenUp)
            await AddTelemetryEnvironmentAsync(runId, legVars, ct).ConfigureAwait(false);
        if (!BlueGreenStepBinding.TryBind(
            operation, stepDef, raw => SubstituteVariables(raw ?? string.Empty, legVars).Trim(),
            legVars, out var binding, out var bindingError))
        {
            await FailAsync(bindingError).ConfigureAwait(false);
            return;
        }
        await BlueGreenRunOwnership.StampAsync(binding!.Variables, runId, resourceLocks, ct).ConfigureAwait(false);
        // Recette R2-092: bluegreen-up refuses a compose_env name the run does not define, so a rollback
        // that finds one undefined belongs to a run that never started a colour, let alone switched
        // one. The agent's rollback says there is nothing to undo; a run warning about variables it
        // does not need would only repeat the failure that stopped the run in other words.
        if (binding!.SkippedComposeEnv.Count > 0 && operation != OperationKind.BlueGreenRollback)
            await repo.AppendRunWarningsAsync(runId, [$"Blue-green step '{stepRun.StepName}': compose_env did not forward {string.Join(", ", binding.SkippedComposeEnv)}, which this run does not define at this point."], ct).ConfigureAwait(false);

        stepRun.ServerId = legServer.Id;
        var task = new ServerTask
        {
            ServerId = legServer.Id,
            Name = stepRun.StepName,
            Command = binding!.Project,
            PipelineRunId = runId,
            PipelineStepRunId = stepRun.Id,
            EnvironmentVariables = TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(binding.Variables)),
            TimeoutSeconds = stepDef.TimeoutSeconds,
            Operation = operation
        };
        repo.TrackTask(task);
        MarkStepDispatched(stepRun, task);
    }

    /// <summary>
    /// The subset of the bootstrap variables that carries a credential. The user name and the
    /// expiry are not secrets; the password and the stamp are, whether derived here or supplied by
    /// the pipeline.
    /// </summary>
    private static string[] SecretBearingNames(bool forSmoke) => forSmoke
        ? ["AETHEUS_SMOKE_ADMIN_PASSWORD"]
        : ["DEPLOYMENT_BOOTSTRAP_PASSWORD", "BOOTSTRAP_STAMP"];

    /// <summary>
    /// Fills in the run-scoped bootstrap identity for the two steps that need it, and registers its
    /// secret parts for masking.
    ///
    /// A pipeline that names any of these itself wins: the demo authenticates its smoke with the
    /// credential it publicly advertises, and overwriting that with a synthetic one would make the
    /// probe test an identity nobody else can use. The derived identity fills the gap, it does not
    /// impose itself.
    ///
    /// Registering for masking is what makes this safe to put in a task environment at all: the value
    /// is redacted in logs, in the recorded command and in step output variables, so an accidental
    /// echo cannot publish a working credential for the colour being deployed.
    /// </summary>
    /// <summary>
    /// Adds the OTEL environment a deployed colour needs to push telemetry back to Aetheus, for the
    /// step that starts it. Values come from <see cref="IAppDeployEnvProvider"/>, which returns an
    /// empty map when the feature is unconfigured or no monitored application is linked, so a
    /// deployment that does not opt in is unaffected.
    /// </summary>
    /// <remarks>
    /// Never fatal: telemetry is observability, and failing a production cutover because a metrics
    /// endpoint could not be computed would trade the deployment for its instrumentation. The reason
    /// is logged instead, and the colour starts with telemetry disabled as it did before.
    /// </remarks>
    private async Task AddTelemetryEnvironmentAsync(
        int runId, Dictionary<string, string> legVars, CancellationToken ct)
    {
        try
        {
            var run = await repo.GetPipelineRunWithPipelineAsync(runId, ct).ConfigureAwait(false);
            var projectId = run?.Pipeline is null
                ? null
                : await repo.GetPipelineProjectIdAsync(run.Pipeline, ct).ConfigureAwait(false);
            if (projectId is null) return;

            var telemetryEnv = await appDeployEnv
                .GetDeployEnvAsync(projectId.Value, environmentId: null, runId, ct).ConfigureAwait(false);
            foreach (var (key, value) in telemetryEnv)
                legVars[key] = value;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Telemetry environment injection failed for run {RunId}; the colour starts without it", runId);
        }
    }

    private bool TryAddDeploymentBootstrapIdentity(
        int runId, Dictionary<string, string> legVars, bool forSmoke, out string error)
    {
        error = string.Empty;
        // DEPLOYMENT_BOOTSTRAP_* and not ADMIN_*: the deployed backend seeds its persisted `admin`
        // from Auth:AdminPassword on a first run, so handing it this run's throwaway secret gave a
        // brand-new environment a permanent administrator whose password only that run ever knew -
        // and the run masks it out of its own log. The two identities are now separate everywhere.
        var required = forSmoke
            ? new[] { "AETHEUS_SMOKE_ADMIN_USER", "AETHEUS_SMOKE_ADMIN_PASSWORD" }
            : BlueGreenStepBinding.StartOnlyNames;
        // A pipeline that supplies every name itself needs no derivation, and must not be refused for
        // lacking a key it never uses. It still gets masked: a self-supplied credential is exactly as
        // sensitive as a derived one, and returning before RegisterRuntimeSecret used to publish it in
        // clear in the run log, which is the transport this whole derivation exists to remove.
        if (required.All(name => legVars.TryGetValue(name, out var declared) && !string.IsNullOrEmpty(declared)))
        {
            foreach (var name in SecretBearingNames(forSmoke))
                secretMasking.RegisterRuntimeSecret(runId, legVars[name]);
            return true;
        }

        DeploymentBootstrapIdentity identity;
        try
        {
            identity = DeploymentBootstrapIdentity.For(configuration, runId, timeProvider.GetUtcNow().UtcDateTime);
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            return false;
        }
        secretMasking.RegisterRuntimeSecret(runId, identity.Password);
        secretMasking.RegisterRuntimeSecret(runId, identity.Stamp);

        var mapping = forSmoke
            ? new[]
            {
                ("AETHEUS_SMOKE_ADMIN_USER", identity.User),
                ("AETHEUS_SMOKE_ADMIN_PASSWORD", identity.Password)
            }
            :
            [
                ("DEPLOYMENT_BOOTSTRAP_USER", identity.User),
                ("DEPLOYMENT_BOOTSTRAP_PASSWORD", identity.Password),
                ("BOOTSTRAP_STAMP", identity.Stamp),
                ("DEPLOYMENT_BOOTSTRAP_EXPIRES_AT_UTC", identity.ExpiresAtUtc)
            ];

        foreach (var (name, value) in mapping)
        {
            if (!legVars.TryGetValue(name, out var declared) || string.IsNullOrEmpty(declared))
                legVars[name] = value;
        }
        return true;
    }

}
