// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Components.Pipelines;

internal static class PipelineDefinitionValidator
{
    private static readonly HashSet<string> KnownTypedStepTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "release", "substitute", "coverage", "complexity", "lint", "artifacts",
        "deploy", "apache-proxy", "apache-config", "certbot", "trigger", "restore-artifacts",
        "restore-backup", "scanner", "analysis-gate", "publish-observability", "ai", "smoke",
        "dotnet-test", "gate-status", "mutation",
        "bluegreen-migrate", "bluegreen-up", "bluegreen-switch", "bluegreen-commit", "bluegreen-rollback",
        "bluegreen-retire", "bluegreen-revert", "advance-branch", PipelineFunctionCatalog.StepType
    };

    internal static bool IsKnownStepType(string type) => KnownTypedStepTypes.Contains(type);

    internal static void ValidateDefinitionBasics(
        PipelineYamlDefinition definition,
        ICollection<string> errors,
        ICollection<string> warnings)
    {
        ValidateResourceNames(definition, errors);
        if (definition.Branches.Any(string.IsNullOrWhiteSpace))
            errors.Add("Branch filter entries cannot be empty.");
        if (!string.IsNullOrWhiteSpace(definition.SourceBranch)
            && !PipelineBranchValidator.IsValid(definition.SourceBranch))
            errors.Add("The configured source_branch is invalid.");
        ValidateSource(definition.Source, errors);
        ValidateWebhookFilters(definition, errors, warnings);
        ValidateReleaseNumber(definition, errors);
        if (definition.Stages.Count == 0)
            errors.Add("Pipeline must have at least one stage.");
    }

    // Library and vault names: never empty, and only the expressions a run can resolve.
    private static void ValidateResourceNames(PipelineYamlDefinition definition, ICollection<string> errors)
    {
        if (definition.VariableLibraries.Any(string.IsNullOrWhiteSpace))
            errors.Add("Variable library names cannot be empty.");
        if (definition.Vaults.Any(string.IsNullOrWhiteSpace))
            errors.Add("Vault names cannot be empty.");
        // R3-67: `${{ project.slug }}` is the one expression a library or vault name may carry; any
        // other would reach the lookup as written and never match a resource.
        if (definition.VariableLibraries.Concat(definition.Vaults)
                .Concat(definition.Requires?.Libraries ?? [])
                .Concat(definition.Requires?.Vaults ?? [])
                .Any(PipelineProjectResourceNames.HasUnsupportedExpression))
            errors.Add($"Library and vault names accept only the {PipelineProjectResourceNames.SlugExpression} and ${{{{ parameters.<name> }}}} expressions.");
    }

    // Branch, supersede and path filters only mean something for the webhook trigger.
    private static void ValidateWebhookFilters(
        PipelineYamlDefinition definition,
        ICollection<string> errors,
        ICollection<string> warnings)
    {
        if (definition.Branches.Count > 0
            && !string.Equals(definition.Trigger, "webhook", StringComparison.OrdinalIgnoreCase))
            warnings.Add("`branches:` filter only applies to the webhook trigger; it will be ignored for this trigger type.");
        if (definition.SupersedeRunning == true
            && !string.Equals(definition.Trigger, "webhook", StringComparison.OrdinalIgnoreCase))
            warnings.Add("`supersede_running:` only applies to the webhook trigger; it will be ignored for this trigger type.");
        if (definition.PathsIgnore.Any(string.IsNullOrWhiteSpace))
            errors.Add("Path filter entries cannot be empty.");
        if (definition.PathsIgnore.Count > 0
            && !string.Equals(definition.Trigger, "webhook", StringComparison.OrdinalIgnoreCase))
            warnings.Add("`paths_ignore:` only applies to the webhook trigger; it will be ignored for this trigger type.");
    }

    // The release-number setting accepts one mode.
    private static void ValidateReleaseNumber(PipelineYamlDefinition definition, ICollection<string> errors)
    {
        if (definition.ReleaseNumber is not null && !PipelineReleaseNumbers.Allocates(definition))
            errors.Add(PipelineReleaseNumbers.InvalidModeMessage);
    }

    // The source: block names a repository of the project by its slug. Whether that repository
    // exists is a fact of the installation, checked when a run is prepared.
    private static void ValidateSource(PipelineSourceDefinition? source, ICollection<string> errors)
    {
        if (source is null) return;
        ValidateSourceRepository(source.Repository.Trim(), errors);
        if (!string.IsNullOrWhiteSpace(source.Branch) && !PipelineBranchValidator.IsValid(source.Branch.Trim()))
            errors.Add("The configured source.branch is invalid.");
        ValidateSourceMatchBranch(source, errors);
        ValidateSourceMatchExcludeFile(source, errors);
    }

    private static void ValidateSourceRepository(string repository, ICollection<string> errors)
    {
        if (repository.Length == 0)
            errors.Add("source.repository is required: the slug of a repository attached to the project.");
        else if (repository.Length > 200 || repository.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            errors.Add("source.repository must be a repository slug (letters, digits, '-', '_', '.').");
    }

    private static void ValidateSourceMatchBranch(PipelineSourceDefinition source, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(source.MatchBranch)) return;
        if (!source.MustMatchDefinition)
            errors.Add("source.match_branch is only read when source.must_match_definition is true.");
        if (!PipelineBranchValidator.IsValid(source.MatchBranch.Trim()))
            errors.Add("The configured source.match_branch is invalid.");
    }

    private static void ValidateSourceMatchExcludeFile(PipelineSourceDefinition source, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(source.MatchExcludeFile)) return;
        if (!source.MustMatchDefinition)
            errors.Add("source.match_exclude_file is only read when source.must_match_definition is true.");
        var path = source.MatchExcludeFile.Replace('\\', '/').Trim();
        if (!path.StartsWith(".pipeline/configs/", StringComparison.Ordinal)
            || path.Split('/').Any(segment => segment is "." or ".."))
            errors.Add("source.match_exclude_file must be a file under .pipeline/configs/.");
    }

    /// <summary>Recette R-14: <c>deployment_role</c> is <c>start</c> or <c>rollback</c>, and a rollback stage is a
    /// failure handler, or the run would judge as undone a deployment nothing undid.</summary>
    private static void ValidateDeploymentRole(PipelineStageDefinition stage, List<string> errors)
    {
        if (stage.DeploymentRole is not { } role) return;
        if (!PipelineDeploymentRoles.All.Contains(role.Trim(), StringComparer.OrdinalIgnoreCase))
            errors.Add($"Stage '{stage.Name}': deployment_role must be one of {string.Join(", ", PipelineDeploymentRoles.All)}.");
        else if (string.Equals(role.Trim(), PipelineDeploymentRoles.Rollback, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stage.Condition?.Trim(), "failed()", StringComparison.OrdinalIgnoreCase))
            errors.Add($"Stage '{stage.Name}': deployment_role: rollback requires condition: failed().");
    }

    internal static void ValidateStages(
        PipelineYamlDefinition definition,
        List<string> errors,
        List<string> warnings)
    {
        foreach (var stage in definition.Stages)
        {
            if (string.IsNullOrWhiteSpace(stage.Name))
                errors.Add("Stage name cannot be empty.");
            if (OsTypeHelper.IsUnrecognized(stage.Os))
                warnings.Add($"Stage '{stage.Name}' has an unrecognized os '{stage.Os}' - expected 'linux' or 'windows'; it will be ignored (no OS constraint).");
            // Stage confirmation windows are limited to a day and can exist independently of an
            // environment.
            if (stage.ApprovalTimeoutMinutes is { } minutes && minutes is < 1 or > 1440)
                errors.Add($"Stage '{stage.Name}': approval_timeout_minutes must be between 1 and 1440.");
            // R-39: a missed confirmation waits for a resume at most a week; 0 turns the wait off.
            if (stage.ResumeWindowHours is { } hours && hours is < 0 or > PipelineResumeWindow.MaxHours)
                errors.Add($"Stage '{stage.Name}': resume_window_hours must be between 0 and {PipelineResumeWindow.MaxHours}.");
            else if (stage.ResumeWindowHours is not null && stage.ApprovalTimeoutMinutes is null)
                warnings.Add($"Stage '{stage.Name}': resume_window_hours has no effect without approval_timeout_minutes.");
            ValidateDeploymentRole(stage, errors);
            if (stage.Jobs.Count > 0)
                ValidateJobs(stage, errors, warnings);
            else
                ValidateLegacyStage(stage, errors, warnings);
        }
        foreach (var stage in YamlParsingHelper.FlattenJobs(definition))
            foreach (var step in stage.Steps)
                ValidateArtifactsStep(errors, step, stage, $"stage '{stage.Name}'");
        ValidateArtifactNamesAreUnique(definition, errors);
        // A condition is one supported form on its whole text; a compound or trailing text is
        // refused here rather than half-evaluated at run time.
        errors.AddRange(PipelineConditionEvaluator.FindDefinitionProblems(definition));
    }

    /// <summary>
    /// Two stages of one pipeline may not publish under the same artifact name: the second
    /// publication replaces the first, and the consumer that restores it gets whichever ran last.
    /// Only stages that actually publish are considered - a name declared on a stage with no
    /// artifacts is inert.
    /// </summary>
    private static void ValidateArtifactNamesAreUnique(PipelineYamlDefinition definition, List<string> errors)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in YamlParsingHelper.FlattenJobs(definition))
        {
            // Stage bundles and artifact steps share one artifact-name namespace.
            var publishers = stage.Steps
                .Where(step => IsArtifactsStep(step) && !string.IsNullOrWhiteSpace(step.ArtifactName))
                .Select(step => (Name: step.ArtifactName!.Trim(), Owner: $"step '{step.Name}' of stage '{stage.Name}'"));
            if (stage.Artifacts.Count > 0)
                publishers = publishers.Prepend((PipelineRunScheduler.StageArtifactName(stage), $"stage '{stage.Name}'"));
            foreach (var (name, owner) in publishers)
            {
                if (seen.TryGetValue(name, out var first))
                    errors.Add(
                        $"{Capitalize(first)} and {owner} both publish artifact '{name}'; "
                        + "the second publication would replace the first. Give one of them its own artifact_name.");
                else
                    seen[name] = owner;
            }
        }

        static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static bool IsArtifactsStep(PipelineStepDefinition step) =>
        string.Equals(step.Type, "artifacts", StringComparison.OrdinalIgnoreCase);

    /// <summary>Artifact steps collect named files from the agent workspace, so container-isolated
    /// stages cannot use them.</summary>
    private static void ValidateArtifactsStep(List<string> errors, PipelineStepDefinition step, PipelineStageDefinition stage, string context)
    {
        if (!IsArtifactsStep(step))
        {
            if (!string.IsNullOrWhiteSpace(step.ArtifactName))
                errors.Add($"Step '{step.Name}' in {context} artifact_name is only valid on an artifacts step.");
            return;
        }
        if (string.IsNullOrWhiteSpace(step.ArtifactName))
            errors.Add($"Step '{step.Name}' in {context} requires artifact_name.");
        if (step.TargetFiles.Count == 0)
            errors.Add($"Step '{step.Name}' in {context} requires target_files, the files the artifact holds.");
        if (stage.Isolation?.IsContainer == true)
            errors.Add($"Step '{step.Name}' in {context} cannot collect artifacts in a container-isolated stage; declare them on the stage.");
        // Every leg would publish the same name and the last one would silently replace the others.
        if (stage.Matrix is { Count: > 0 })
            errors.Add($"Step '{step.Name}' in {context} cannot collect artifacts in a matrix: each leg would replace the previous one.");
    }

    private static void ValidateJobs(
        PipelineStageDefinition stage,
        List<string> errors,
        List<string> warnings)
    {
        foreach (var job in stage.Jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Name))
                errors.Add($"Job name cannot be empty in stage '{stage.Name}'.");
            if (string.IsNullOrWhiteSpace(job.Agent)
                && string.IsNullOrWhiteSpace(job.Pool)
                && string.IsNullOrWhiteSpace(job.Environment)
                && string.IsNullOrWhiteSpace(stage.Agent)
                && string.IsNullOrWhiteSpace(stage.Pool)
                && string.IsNullOrWhiteSpace(stage.Environment))
                warnings.Add($"Job '{job.Name}' in stage '{stage.Name}' has no agent/pool/environment - will use run affinity.");
            if (OsTypeHelper.IsUnrecognized(job.Os))
                warnings.Add($"Job '{job.Name}' in stage '{stage.Name}' has an unrecognized os '{job.Os}' - expected 'linux' or 'windows'; it will be ignored (no OS constraint).");
            if (job.Steps.Count == 0)
                errors.Add($"Job '{job.Name}' in stage '{stage.Name}' must have at least one step.");
            ValidateSteps(
                errors, warnings, job.Steps,
                $"stage '{stage.Name}' / job '{job.Name}'", stage.Environment);
        }
    }

    private static void ValidateLegacyStage(
        PipelineStageDefinition stage,
        List<string> errors,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(stage.Agent)
            && string.IsNullOrWhiteSpace(stage.Pool)
            && string.IsNullOrWhiteSpace(stage.Environment))
            warnings.Add($"Stage '{stage.Name}' has no agent/pool/environment - will use run affinity.");
        if (stage.Steps.Count == 0)
            errors.Add($"Stage '{stage.Name}' must have at least one step.");
        ValidateSteps(errors, warnings, stage.Steps, $"stage '{stage.Name}'", stage.Environment);
    }

    private static void ValidateSteps(
        List<string> errors,
        List<string> warnings,
        List<PipelineStepDefinition> steps,
        string context,
        string? environment)
    {
        foreach (var step in steps)
        {
            ValidateStepBasics(errors, warnings, step, context, environment);
            ValidateDeclaredOutputs(errors, step, context);
            ValidateApacheConfigStep(errors, step, context);
            ValidateEnvTemplateStep(errors, step, context);
            ValidateRestoreArtifactsStep(errors, step, context);
            ValidateTriggerStep(errors, step, context);
            ValidateAdvanceBranchStep(errors, step, context);
        }
    }

    private static void ValidateStepBasics(
        List<string> errors,
        List<string> warnings,
        PipelineStepDefinition step,
        string context,
        string? environment)
    {
        if (string.IsNullOrWhiteSpace(step.Name))
            errors.Add($"Step name cannot be empty in {context}.");
        // A type this backend does not know yet (written for a newer one) is a warning, not an
        // invalid definition, so adding a step type never blocks the delivery that brings it. The
        // step itself fails visibly if it ever runs here (PipelineStepTaskDispatcher).
        if (!string.IsNullOrWhiteSpace(step.Type) && !IsKnownStepType(step.Type))
            warnings.Add($"Step '{step.Name}' in {context} uses the unknown type '{step.Type}'; it fails if it runs on this backend.");
        else if (string.IsNullOrWhiteSpace(step.Shell) && !step.Checkout && string.IsNullOrWhiteSpace(step.Type))
            errors.Add($"Step '{step.Name}' in {context} must have a shell command or checkout enabled.");
        if (step.RetryCount < 0)
            errors.Add($"Step '{step.Name}' retry_count cannot be negative.");
        WarnAboutInlineLogic(warnings, step, context);
        PipelineAiStepValidator.Validate(errors, step, context);
        PipelineFunctionStepValidator.Validate(errors, step, context);
        PipelineStepAnalysisValidator.Validate(errors, step, context, environment);
        if (string.Equals(step.Type, "coverage", StringComparison.OrdinalIgnoreCase)
            && step.MinCoverage is > 0)
            warnings.Add(
                $"Step '{step.Name}' in {context} uses legacy min_coverage. " +
                "Keep it temporarily for compatibility, then move the threshold to a versioned quality gate.");
    }

    /// <summary>
    /// <c>outputs:</c> widens what the launch check accepts, so it is held to the shape a published
    /// name has and to a bound: a typo or a lower-case name would otherwise exempt a reference nothing
    /// will ever satisfy.
    /// </summary>
    private static void ValidateDeclaredOutputs(List<string> errors, PipelineStepDefinition step, string context)
    {
        if (step.Outputs.Count > PipelineUnresolvedVariableGuard.MaxDeclaredOutputs)
            errors.Add($"Step '{step.Name}' in {context} declares {step.Outputs.Count} outputs, more than the {PipelineUnresolvedVariableGuard.MaxDeclaredOutputs} allowed.");
        foreach (var output in step.Outputs)
            if (output is null || !PipelineUnresolvedVariableGuard.DeclaredOutputName().IsMatch(output))
                errors.Add($"Step '{step.Name}' in {context} output '{output}' must be an upper snake case variable name.");
        var duplicate = step.Outputs.GroupBy(output => output, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            errors.Add($"Step '{step.Name}' in {context} declares output '{duplicate.Key}' twice.");
    }

    /// <summary>How long a shell step may get before it stops being a call and starts being a
    /// program. Thirty lines, not the five the convention asks for: this is a warning shown to every
    /// project, and it must fire on what is genuinely unmaintainable rather than on style.</summary>
    private const int InlineShellLineBudget = 30;

    /// <summary>
    /// Warns about logic living inside a pipeline definition instead of a tested script or a typed
    /// step. A YAML is not versioned, not tested and not reusable: the same forty-line block copied
    /// into a second pipeline is how a fix reaches one of them and not the other.
    ///
    /// A warning, never an error. Refusing these would break every existing definition, including
    /// this repository's own, which is exactly the migration this warning exists to guide.
    /// </summary>
    private static void WarnAboutInlineLogic(
        List<string> warnings, PipelineStepDefinition step, string context)
    {
        if (string.IsNullOrWhiteSpace(step.Shell)) return;

        var lineCount = step.Shell.AsSpan().Count('\n') + 1;
        if (lineCount > InlineShellLineBudget)
            warnings.Add(
                $"Step '{step.Name}' in {context} inlines {lineCount} lines of shell. "
                + "Move it to a tested script of the repository (or a typed step) and call it: "
                + "a shell block in a definition is not versioned, not tested and not reusable.");

        if (ContainsHeredoc(step.Shell))
            warnings.Add(
                $"Step '{step.Name}' in {context} embeds a heredoc. "
                + "Whatever it writes belongs in a file of its own, tested; a heredoc hides a second "
                + "language inside the definition, where nothing checks it.");
    }

    /// <summary>A shell heredoc opener (<c>&lt;&lt;EOF</c>, <c>&lt;&lt;-'EOF'</c>, <c>&lt;&lt;"EOF"</c>).
    /// Matched on the operator plus a delimiter word, so a plain redirection or a comparison does not
    /// trip it.</summary>
    private static bool ContainsHeredoc(string shell)
    {
        for (var index = shell.IndexOf("<<", StringComparison.Ordinal); index >= 0;
             index = shell.IndexOf("<<", index + 2, StringComparison.Ordinal))
        {
            var rest = shell.AsSpan(index + 2);
            if (rest.Length > 0 && rest[0] == '-') rest = rest[1..];
            if (rest.Length > 0 && (rest[0] == '\'' || rest[0] == '"')) rest = rest[1..];
            if (rest.Length > 0 && (char.IsLetter(rest[0]) || rest[0] == '_')) return true;
        }
        return false;
    }

    // env_template is the sample a shell step's environment file is rendered from (ADR-057): only a plain
    // shell step can receive it, and its path is a versioned file below .pipeline/configs/.
    private static void ValidateEnvTemplateStep(
        ICollection<string> errors,
        PipelineStepDefinition step,
        string context)
    {
        if (string.IsNullOrWhiteSpace(step.EnvTemplate)) return;
        if (!string.IsNullOrWhiteSpace(step.Type))
            errors.Add($"Step '{step.Name}' in {context} env_template is only valid on a plain shell step, not on type '{step.Type}'.");
        if (!step.EnvTemplate.Contains("$(", StringComparison.Ordinal)
            && !PipelineConfigTemplateRenderer.IsSafeConfigPath(step.EnvTemplate))
            errors.Add($"Step '{step.Name}' in {context} env_template '{step.EnvTemplate}' must be under .pipeline/configs/.");
        if (!step.Shell.Contains(PipelineEnvFilePreparer.VariableName, StringComparison.Ordinal))
            errors.Add($"Step '{step.Name}' in {context} has an env_template but its shell never reads {PipelineEnvFilePreparer.VariableName}, so the file would reach nobody.");
    }

    private static void ValidateApacheConfigStep(
        ICollection<string> errors,
        PipelineStepDefinition step,
        string context)
    {
        if (!string.Equals(step.Type, "apache-config", StringComparison.OrdinalIgnoreCase))
            return;
        if (step.ConfigFiles.Count == 0)
            errors.Add($"Step '{step.Name}' in {context} requires at least one config_files entry.");
        foreach (var (siteFile, templatePath) in step.ConfigFiles)
        {
            if (!IsSafeApacheDestinationPattern(siteFile))
                errors.Add($"Step '{step.Name}' in {context} has an invalid Apache destination '{siteFile}'.");
            if (!PipelineConfigTemplateRenderer.IsSafeConfigPath(templatePath))
                errors.Add($"Step '{step.Name}' in {context} template '{templatePath}' must be under .pipeline/configs/.");
        }
    }

    private static void ValidateRestoreArtifactsStep(
        ICollection<string> errors,
        PipelineStepDefinition step,
        string context)
    {
        if (!string.Equals(step.Type, "restore-artifacts", StringComparison.OrdinalIgnoreCase))
        {
            if (step.DeployedBaseline is not null)
                errors.Add($"Step '{step.Name}' in {context} deployed_baseline is only valid on a restore-artifacts step.");
            return;
        }
        var hasReleaseSource = !string.IsNullOrWhiteSpace(step.Release);
        if (hasReleaseSource && !string.IsNullOrWhiteSpace(step.ArtifactSourcePipeline))
            errors.Add($"Step '{step.Name}' in {context} cannot combine 'release' with 'artifact_source_pipeline'.");
        else if (!hasReleaseSource
                 && (string.IsNullOrWhiteSpace(step.Artifact)
                     || string.IsNullOrWhiteSpace(step.ArtifactSourcePipeline)))
            errors.Add($"Step '{step.Name}' in {context} requires 'release' or both 'artifact' and 'artifact_source_pipeline'.");
        ValidateArtifactSourceSelector(errors, step, context, hasReleaseSource);
        if (!PipelinePathValidation.IsSafeRelativeDirectory(step.TargetDirectory))
            errors.Add($"Step '{step.Name}' in {context} target_directory must be a safe relative path.");
        if (step.AllowMissing
            && string.IsNullOrWhiteSpace(step.ArtifactSourcePipeline)
            && !BootstrapReleaseSelectors.Contains(step.Release?.Trim() ?? string.Empty))
            errors.Add($"Step '{step.Name}' in {context} allow_missing requires artifact_source_pipeline or release: latest-published, previous-published, previous-deployed, or current-deployed.");
        ValidateDeployedBaseline(errors, step, context);
    }

    /// <summary>The deployed-baseline export only exists for the two deployed selectors, so the key is
    /// refused anywhere else rather than ignored: written elsewhere it would read as a switch that
    /// does nothing. An unknown value is refused too, never mapped to the default.</summary>
    private static void ValidateDeployedBaseline(
        ICollection<string> errors,
        PipelineStepDefinition step,
        string context)
    {
        if (step.DeployedBaseline is null) return;
        if (!PipelineDeployedBaseline.IsValid(step.DeployedBaseline) || string.IsNullOrWhiteSpace(step.DeployedBaseline))
            errors.Add(
                $"Step '{step.Name}' in {context} deployed_baseline must be "
                + $"'{PipelineDeployedBaseline.Required}' or '{PipelineDeployedBaseline.None}'.");
        if (!DeployedReleaseSelectors.Contains(step.Release?.Trim() ?? string.Empty))
            errors.Add(
                $"Step '{step.Name}' in {context} deployed_baseline requires release: previous-deployed or current-deployed.");
    }

    private static readonly HashSet<string> DeployedReleaseSelectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "previous-deployed", "current-deployed"
    };

    /// <summary>Release selectors that may legitimately resolve to nothing, so `allow_missing` on
    /// them is a bootstrap and not a way to hide a broken reference.</summary>
    private static readonly HashSet<string> BootstrapReleaseSelectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "latest-published", "previous-published", "previous-deployed", "current-deployed"
    };

    private static void ValidateArtifactSourceSelector(
        ICollection<string> errors,
        PipelineStepDefinition step,
        string context,
        bool hasReleaseSource)
    {
        var selector = step.ArtifactSourceSelector?.Trim();
        if (string.IsNullOrEmpty(selector)) return;

        if (!string.Equals(selector, PipelineArtifactTaskFactory.SameCommitSelector, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(selector, PipelineArtifactTaskFactory.LatestSuccessfulSelector, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"Step '{step.Name}' in {context} artifact_source_selector must be "
                + $"'{PipelineArtifactTaskFactory.SameCommitSelector}' or "
                + $"'{PipelineArtifactTaskFactory.LatestSuccessfulSelector}'.");
        }
        // Refused rather than ignored: a selector written beside a `release:` reads as if it widened
        // that lookup, and silently dropping it is how a definition ends up meaning something other
        // than what it says.
        if (hasReleaseSource)
            errors.Add($"Step '{step.Name}' in {context} cannot combine 'release' with 'artifact_source_selector'.");
    }

    private static void ValidateTriggerStep(
        ICollection<string> errors,
        PipelineStepDefinition step,
        string context)
    {
        if (!string.Equals(step.Type, "trigger", StringComparison.OrdinalIgnoreCase))
            return;
        if (string.IsNullOrWhiteSpace(step.Pipeline))
            errors.Add($"Step '{step.Name}' in {context} requires a pipeline name.");
        if (step.InheritSource && !string.IsNullOrWhiteSpace(step.SourceBranch))
            errors.Add($"Step '{step.Name}' in {context} source_branch requires inherit_source: false.");
        if (step.InheritSource && !string.IsNullOrWhiteSpace(step.SourceCommit))
            errors.Add($"Step '{step.Name}' in {context} source_commit requires inherit_source: false.");
        if (!step.InheritSource && !PipelineBranchValidator.IsValid(step.SourceBranch ?? string.Empty))
            errors.Add($"Step '{step.Name}' in {context} requires a valid source_branch when inherit_source is false.");
    }

    // The branch is required. A literal name is checked here; one built from a variable is checked
    // when the step runs, where the backend refuses a name git would not take.
    private static void ValidateAdvanceBranchStep(
        ICollection<string> errors,
        PipelineStepDefinition step,
        string context)
    {
        if (!string.Equals(step.Type, "advance-branch", StringComparison.OrdinalIgnoreCase))
        {
            // Decision of 2026-10-07: the release tag is written by the advance step, nowhere else.
            if (!string.IsNullOrWhiteSpace(step.Tag))
                errors.Add($"Step '{step.Name}' in {context} sets 'tag', which only a type: advance-branch step takes.");
            return;
        }
        var branch = step.Branch?.Trim();
        if (string.IsNullOrEmpty(branch))
            errors.Add($"Step '{step.Name}' in {context} requires a branch to advance.");
        else if (!branch.Contains("$(", StringComparison.Ordinal) && !PipelineBranchValidator.IsValid(branch))
            errors.Add($"Step '{step.Name}' in {context} names an invalid branch '{branch}'.");
    }

    internal static bool IsSafeApacheDestinationPattern(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 220
            || value.Contains('/') || value.Contains('\\') || value.Contains("..", StringComparison.Ordinal))
            return false;
        var probe = System.Text.RegularExpressions.Regex.Replace(
            value, @"\$\([A-Za-z_][A-Za-z0-9_.-]*(?::-[A-Za-z0-9_.-]*)?\)", "value");
        return OperationTargetValidator.ApacheSiteFileRegex().IsMatch(probe);
    }
}
