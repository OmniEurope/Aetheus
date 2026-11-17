// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-11 (decision of 2026-11-10, option A, "watch out for security"): what a pipeline definition read from an
/// external repository's mirror may ask for. Such a file is untrusted input: anyone who can push to the external
/// repository writes it. It is imported read-only, and only when its effective definition (after the organization's
/// catalogue templates are resolved) stays inside a build-and-check scope. The policy denies by default every field
/// that grants a privilege, and allows only the following:
/// <list type="bullet">
/// <item>pipeline level: <c>name</c>, <c>extends</c> (a catalogue template of the organization), <c>parameters</c>,
/// non-secret <c>variables</c>, <c>project_type</c>, <c>supersede_running</c>, <c>source_branch</c> (only a label: the
/// run is pinned to the imported commit) and <c>stages</c>; <c>trigger</c>, <c>schedule</c>, <c>branches</c> and
/// <c>paths_ignore</c> are accepted but neutralized (below);</item>
/// <item>stage and job level: <c>name</c>, <c>condition</c>, <c>depends_on</c>, <c>variables</c>, <c>steps</c>,
/// <c>jobs</c>, <c>agent: default</c> (or no agent), <c>execution_role: build</c> (or none),
/// <c>approval_timeout_minutes</c> and <c>resume_window_hours</c> (inert without an environment);</item>
/// <item>step level: untyped shell steps and the build, test and analysis types of <see cref="AllowedStepTypes"/>,
/// with their own settings, except the fields refused below.</item>
/// </list>
/// Refused, because each one reaches beyond the project's own build:
/// <list type="bullet">
/// <item>secrets and shared configuration: <c>variable_libraries</c>, <c>vaults</c>, <c>requires</c> (libraries,
/// vaults, environments and runner capabilities);</item>
/// <item>other pipelines, repositories and releases: <c>on_success</c>, <c>source</c>, <c>release_number</c>
/// (it would take the project's release numbers), the <c>trigger</c> step type;</item>
/// <item>runner choice: <c>pool</c>, <c>environment</c>, a named <c>agent</c>, <c>os</c>, <c>isolation</c> (image,
/// toolchain, network, runtime, limits; pipeline, stage or job), <c>matrix</c>, <c>strategy</c>, <c>group</c>,
/// <c>observed_port_policy</c>, a deployment role and an execution role other than <c>build</c>; the run's servers
/// are the ones linked to the project (dispatch enforces it, see <c>PipelineDispatchServerResolver</c>);</item>
/// <item>published outputs another pipeline could deploy: the <c>artifacts</c> step type and the stage or job
/// <c>artifacts</c> / <c>artifact_name</c> bundles, since a <c>restore-artifacts</c> step of any pipeline of the
/// project (<c>artifact_source_pipeline</c>, selector <c>latest-successful</c> or <c>same-commit</c>) restores them
/// by pipeline name;</item>
/// <item>network targets: a scanner's <c>target_url</c>, <c>api_specification_url(s)</c>, <c>active</c>, and any
/// <c>origin</c> / <c>api_origin</c>;</item>
/// <item>every step type that deploys, writes Git, renders configuration files, runs an Aetheus function
/// (<c>type: aetheus</c>, all of them) or calls an AI profile.</item>
/// </list>
/// The same check runs at the import, at every run preparation and again on the definition the caller's run
/// parameters resolve to, because a catalogue template the definition extends, or a parameter a selector reads, can
/// change between them. Triggers are not refused but neutralized: an imported pipeline is stored
/// <see cref="PipelineTriggerType.Manual"/>, its definition can never be edited, and the automatic trigger queries
/// skip it, so a <c>trigger:</c>, <c>schedule:</c> or <c>branches:</c> in the file never makes it fire.
/// </summary>
internal static class PipelineExternalImportPolicy
{
    internal const string PipelineDirectory = ".pipeline/";

    /// <summary>Untyped steps are shell scripts; the typed ones below build, test or analyse the workspace and
    /// publish their results to Aetheus, nothing another pipeline can restore.</summary>
    internal static readonly IReadOnlySet<string> AllowedStepTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "coverage", "complexity", "lint", "scanner", "analysis-gate", "dotnet-test", "gate-status", "mutation"
    };

    /// <summary>A file directly under <c>.pipeline/</c>, YAML, no sub-directory, no traversal.</summary>
    internal static bool IsSafeFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 255) return false;
        if (!path.StartsWith(PipelineDirectory, StringComparison.Ordinal)) return false;
        var fileName = path[PipelineDirectory.Length..];
        return fileName.Length > 0
               && fileName.IndexOfAny(['/', '\\', ':']) < 0
               && fileName is not ("." or "..")
               && !fileName.StartsWith('.')
               && (fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                   || fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The reasons the effective definition is refused; empty when it may be imported or run.</summary>
    internal static List<string> Validate(PipelineYamlDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var problems = new List<string>();
        if (definition.VariableLibraries.Count > 0)
            problems.Add("An external definition may not read variable libraries (variable_libraries).");
        if (definition.Vaults.Count > 0)
            problems.Add("An external definition may not read vaults (vaults).");
        if (definition.Requires is { IsEmpty: false })
            problems.Add("An external definition may not require libraries, vaults, environments or runner capabilities (requires).");
        if (definition.OnSuccess.Count > 0)
            problems.Add("An external definition may not start other pipelines (on_success).");
        if (definition.Source is not null)
            problems.Add("An external definition may not take its workspace from another repository (source).");
        if (definition.Isolation is not null)
            problems.Add("An external definition may not choose its isolation (isolation).");
        if (!string.IsNullOrWhiteSpace(definition.ReleaseNumber))
            problems.Add("An external definition may not allocate release numbers (release_number).");
        if (!string.IsNullOrWhiteSpace(definition.ObservedPortPolicy))
            problems.Add("An external definition may not relax the port guard (observed_port_policy).");

        foreach (var stage in definition.Stages)
        {
            var where = $"stage '{stage.Name}'";
            ValidateTarget(where, new(stage.Pool, stage.Environment, stage.ExecutionRole, stage.Agent, stage.Os,
                stage.Isolation, stage.Matrix, stage.Strategy, stage.Artifacts), problems);
            if (!string.IsNullOrWhiteSpace(stage.DeploymentRole))
                problems.Add($"{where}: an external definition may not take a deployment role.");
            if (!string.IsNullOrWhiteSpace(stage.Group))
                problems.Add($"{where}: an external definition may not join a concurrency group (group).");
            if (!string.IsNullOrWhiteSpace(stage.ArtifactName))
                problems.Add($"{where}: an external definition may not publish an artifact bundle (artifact_name).");
            ValidateSteps(where, stage.Steps, problems);
            foreach (var job in stage.Jobs)
            {
                var jobWhere = $"job '{job.Name}' of stage '{stage.Name}'";
                ValidateTarget(jobWhere, new(job.Pool, job.Environment, job.ExecutionRole, job.Agent, job.Os,
                    job.Isolation, job.Matrix, job.Strategy, job.Artifacts), problems);
                ValidateSteps(jobWhere, job.Steps, problems);
            }
        }
        return problems.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>An imported pipeline is never edited in Aetheus (the user re-imports it from the mirror instead): its
    /// stored definition is exactly the file that was reviewed, and its trigger stays manual.</summary>
    internal static void EnsureEditable(Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (pipeline.IsExternalImport)
            throw new ConflictException(
                $"Pipeline '{pipeline.Name}' was imported read-only from {pipeline.ExternalImportPath} of an external repository: "
                + "it cannot be edited in Aetheus. Change the file in the external repository, then import it again.");
    }

    /// <summary>What a stage or a job says about where and how it runs.</summary>
    private sealed record Target(
        string? Pool, string? Environment, string? ExecutionRole, string? Agent, string? Os,
        PipelineIsolationDefinition? Isolation, Dictionary<string, List<string>>? Matrix,
        PipelineDeploymentStrategy? Strategy, List<string> Artifacts);

    private static void ValidateTarget(string where, Target target, ICollection<string> problems)
    {
        if (!string.IsNullOrWhiteSpace(target.Pool))
            problems.Add($"{where}: an external definition may not select an agent pool (pool).");
        if (!string.IsNullOrWhiteSpace(target.Environment))
            problems.Add($"{where}: an external definition may not target an environment (environment).");
        // The build role is what an ordinary build stage declares; any other role (deploy) is a deployment.
        if (!string.IsNullOrWhiteSpace(target.ExecutionRole)
            && !target.ExecutionRole.Equals("build", StringComparison.OrdinalIgnoreCase))
            problems.Add($"{where}: an external definition may only take the build execution role (execution_role).");
        if (!string.IsNullOrWhiteSpace(target.Agent) && !target.Agent.Equals("default", StringComparison.OrdinalIgnoreCase))
            problems.Add($"{where}: an external definition may not name an agent (agent).");
        if (!string.IsNullOrWhiteSpace(target.Os))
            problems.Add($"{where}: an external definition may not select a runner operating system (os).");
        if (target.Isolation is not null)
            problems.Add($"{where}: an external definition may not choose its isolation (isolation).");
        if (target.Matrix is { Count: > 0 })
            problems.Add($"{where}: an external definition may not fan out (matrix).");
        if (target.Strategy is not null)
            problems.Add($"{where}: an external definition may not declare a deployment strategy (strategy).");
        if (target.Artifacts.Count > 0)
            problems.Add($"{where}: an external definition may not publish an artifact bundle (artifacts).");
    }

    private static void ValidateSteps(string where, IEnumerable<PipelineStepDefinition> steps, ICollection<string> problems)
    {
        foreach (var step in steps)
        {
            var stepWhere = $"{where}, step '{step.Name}'";
            if (!string.IsNullOrWhiteSpace(step.Type) && !AllowedStepTypes.Contains(step.Type))
                problems.Add($"{stepWhere}: the step type '{step.Type}' is not allowed in an external definition.");
            if (step.ConfigFiles.Count > 0 || !string.IsNullOrWhiteSpace(step.EnvTemplate)
                || !string.IsNullOrWhiteSpace(step.ConfigTemplate))
                problems.Add($"{stepWhere}: an external definition may not render configuration files.");
            if (!string.IsNullOrWhiteSpace(step.TargetUrl) || !string.IsNullOrWhiteSpace(step.ApiSpecificationUrl)
                || step.ApiSpecificationUrls.Count > 0 || step.Active)
                problems.Add($"{stepWhere}: an external definition may not aim a scanner at a network target "
                             + "(target_url, api_specification_url, api_specification_urls, active).");
            if (!string.IsNullOrWhiteSpace(step.Origin) || !string.IsNullOrWhiteSpace(step.ApiOrigin))
                problems.Add($"{stepWhere}: an external definition may not probe an origin (origin, api_origin).");
        }
    }
}
