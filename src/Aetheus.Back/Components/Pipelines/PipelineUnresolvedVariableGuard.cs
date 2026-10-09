// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Refuses a launch whose variables still reference a name nothing can ever provide.
/// <para>
/// <see cref="PipelineCommandBuilder.Substitute"/> leaves an unknown <c>$(NAME)</c> LITERAL rather
/// than empty, so a pipeline whose Variable Library entry was never created does not fail: it renders
/// an Apache vhost actually named <c>$(API_HOST)</c> (valid syntax, <c>configtest</c> accepts it) or
/// bakes the placeholder into an image tag, and reports success. That has already happened here once
/// (commit d8904c46, noted in aetheus-release-fast.yaml).
/// </para>
/// <para>
/// The check is deliberately narrow, because plenty of unresolved references are legitimate at the
/// moment variables are resolved. A name is accepted when the pipeline can still provide it: a
/// declared run parameter, a system/agent/project variable injected later in the run, or a name a
/// step publishes at run time through a <c>##aetheus[setvariable]</c> directive. What remains is a
/// reference no stage, parameter, library or system variable will ever satisfy.
/// </para>
/// </summary>
internal static partial class PipelineUnresolvedVariableGuard
{
    /// <summary>
    /// UPPER_SNAKE only, deliberately narrower than <see cref="PipelineCommandBuilder.Substitute"/>'s
    /// own pattern. A variable value is re-substituted into the step's shell text, so <c>$(date)</c>
    /// or <c>$(pwd)</c> inside one is a legitimate POSIX command substitution the shell executes, not
    /// a broken Aetheus reference - and every host- or environment-dependent name this guard exists
    /// for (<c>API_HOST</c>, <c>PORT_FRONT_BLUE</c>, <c>HOST_PREFIX</c>, the system variables) is
    /// upper snake case. Shell builtins and executables are lower case, so the two cannot collide.
    /// Run parameters are camelCase and are exempted by name instead.
    /// A reference carrying a default (<c>$(NAME:-value)</c>) always resolves to something, so it is
    /// matched only to be skipped, never reported.
    /// </summary>
    [GeneratedRegex(@"\$\(([A-Z][A-Z0-9_]*)(:-[^()]*)?\)")]
    private static partial Regex VariableReference();

    /// <summary>What a step may declare under <c>outputs:</c>: the names its scripts publish.</summary>
    [GeneratedRegex(@"^[A-Z][A-Z0-9_]*$")]
    internal static partial Regex DeclaredOutputName();

    /// <summary>Bounds <c>outputs:</c>; a step publishing more is describing something else.</summary>
    internal const int MaxDeclaredOutputs = 32;

    /// <summary>Names a step publishes at run time: <c>##aetheus[setvariable name=X]VALUE</c>.</summary>
    [GeneratedRegex(@"##aetheus\[setvariable\s+name=([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex PublishedName();

    /// <summary>
    /// Variables the control plane injects itself, at a point that may be later than this resolution.
    /// <c>WORKSPACE</c> and <c>BUILD_PROJECTID</c> need a project, <c>BUILD_SOURCEVERSION</c> needs a
    /// resolved commit, and the <c>AGENT_*</c>/<c>SYSTEM_STAGENAME</c> set is injected per stage once a
    /// runner is picked - see <see cref="PipelineVariableResolver"/> and
    /// <see cref="PipelineProjectVariables"/>. Referencing one of them is never the bug this guard
    /// looks for.
    /// </summary>
    private static readonly HashSet<string> ProvidedByTheControlPlane = new(StringComparer.OrdinalIgnoreCase)
    {
        "BUILD_BUILDID", "BUILD_BUILDNUMBER", "BUILD_PIPELINE_RUNNUMBER", "BUILD_RELEASENUMBER", "BUILD_PIPELINEID",
        "BUILD_PIPELINENAME", "BUILD_RUN_PORT", "BUILD_TRIGGEREDBY", "BUILD_PROJECTID",
        "BUILD_PROJECTNAME", "BUILD_SOURCEVERSION", "BUILD_SOURCEBRANCH", "BUILD_REPOSITORY_URI",
        "REPOSITORY_URL", "DEFAULT_BRANCH", "WORKSPACE", "PROJECT_TYPE", "UPSTREAM_RELEASE",
        "SYSTEM_DATE", "SYSTEM_DATETIME", "SYSTEM_TIMESTAMP", "SYSTEM_STAGENAME",
        "AGENT_NAME", "AGENT_HOSTNAME", "AGENT_OS", "AGENT_PLATFORM", "AGENT_ID",
        "AETHEUS_VERSION", "CI",
        // Set on a run whose workspace comes from another repository (source: block, recette R-534).
        "AETHEUS_DEFINITION_COMMIT", "AETHEUS_DEFINITION_BRANCH"
    };

    internal static void Validate(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolved,
        IReadOnlySet<string> secretKeys)
    {
        var failures = FindUnprovidableNames(definition, resolved, secretKeys);
        if (failures.Count == 0) return;

        var detail = failures.Select(entry =>
            $"'{entry.Key}' (referenced by {string.Join(", ", entry.Value)})");
        throw new BadRequestException(
            "Pipeline variable(s) could not be resolved: "
            + string.Join("; ", detail)
            + ". A host- or environment-dependent value must come from a Variable Library the pipeline "
            + "lists under `variable_libraries:`; check that the library exists, is accessible to this "
            + "project, and carries the entry.");
    }

    /// <summary>Names referenced by a variable value that nothing in the run can ever supply, mapped
    /// to the variables that reference them. Internal so its unit tests can assert on the names
    /// rather than on the wording of the exception.</summary>
    internal static SortedDictionary<string, SortedSet<string>> FindUnprovidableNames(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolved,
        IReadOnlySet<string> secretKeys)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var deferred = BuildDeferredNames(definition);
        var failures = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in resolved)
        {
            // A decrypted Vault secret is opaque data, not a template: a passphrase that happens to
            // contain "$(" is a valid secret, and refusing the launch over it would both be wrong and
            // invite the value into a diagnostic message.
            if (secretKeys.Contains(key)) continue;
            Collect(value, $"variable {key}", resolved, deferred, failures);
        }

        // Steps too, not only variables: aetheus-release-fast exports the blue-green ports straight
        // into its shell (`export PORT_FRONT_BLUE="$(PORT_FRONT_BLUE)"`) without any variable of its
        // own referencing them, so a missing library entry would otherwise reach the host unnoticed.
        foreach (var stage in YamlParsingHelper.FlattenJobs(definition))
            foreach (var step in stage.Steps)
            {
                Collect(step.Shell, $"step '{step.Name}'", resolved, deferred, failures);
                Collect(step.WorkingDirectory, $"step '{step.Name}'", resolved, deferred, failures);
                // The rendered vhost CONTENT is already fail-closed (RenderStrict throws on an
                // undefined #{TOKEN}#), but the destination file name is not: a missing DOCS_DOMAIN
                // would install a vhost literally called "$(DOCS_DOMAIN).conf".
                foreach (var (target, source) in step.ConfigFiles)
                {
                    Collect(target, $"step '{step.Name}'", resolved, deferred, failures);
                    Collect(source, $"step '{step.Name}'", resolved, deferred, failures);
                }
                // The pipeline a step triggers or restores from is substituted at dispatch too. The
                // launch preflight judges the expanded name's existence and leaves a reference only
                // known later to the step; a reference nothing can provide is refused here instead.
                Collect(step.Pipeline, $"step '{step.Name}'", resolved, deferred, failures);
                Collect(step.ArtifactSourcePipeline, $"step '{step.Name}'", resolved, deferred, failures);
            }

        return failures;
    }

    /// <summary>True when <paramref name="value"/> still references a name absent from
    /// <paramref name="resolved"/>: a value a launch-time resolution left for the run to complete (a
    /// stage output, a control-plane variable), so it has no value yet.</summary>
    internal static bool ReferencesAnUnresolvedName(string? value, IReadOnlyDictionary<string, string> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (string.IsNullOrEmpty(value) || !value.Contains("$(", StringComparison.Ordinal)) return false;
        foreach (Match match in VariableReference().Matches(value))
            if (!match.Groups[2].Success && !resolved.ContainsKey(match.Groups[1].Value))
                return true;
        return false;
    }

    private static void Collect(
        string? text,
        string origin,
        IReadOnlyDictionary<string, string> resolved,
        IReadOnlySet<string> deferred,
        SortedDictionary<string, SortedSet<string>> failures)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("$(", StringComparison.Ordinal)) return;
        foreach (Match match in VariableReference().Matches(text))
        {
            if (match.Groups[2].Success) continue;
            var name = match.Groups[1].Value;
            if (resolved.ContainsKey(name) || deferred.Contains(name)) continue;
            if (!failures.TryGetValue(name, out var referencedBy))
                failures[name] = referencedBy = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            referencedBy.Add(origin);
        }
    }

    /// <summary>Names the run may still provide after resolution: control-plane variables injected
    /// later, run parameters, and every name a step publishes - through an inline
    /// <c>##aetheus[setvariable]</c> directive or, for a step whose script publishes it, through its
    /// declared <c>outputs:</c>.</summary>
    internal static HashSet<string> BuildDeferredNames(PipelineYamlDefinition definition)
    {
        var deferred = new HashSet<string>(ProvidedByTheControlPlane, StringComparer.OrdinalIgnoreCase);

        // A required parameter without a default has no value until the caller supplies one, so it is
        // absent from a preflight resolution while being perfectly valid.
        foreach (var parameter in definition.Parameters)
            if (!string.IsNullOrWhiteSpace(parameter.Name))
                deferred.Add(parameter.Name);

        foreach (var step in YamlParsingHelper.FlattenJobs(definition).SelectMany(stage => stage.Steps))
        {
            // A name published from a script file is invisible in the step text; the step says so.
            foreach (var output in step.Outputs)
                if (!string.IsNullOrWhiteSpace(output))
                    deferred.Add(output.Trim());
            if (string.IsNullOrEmpty(step.Shell)) continue;
            foreach (Match match in PublishedName().Matches(step.Shell))
                deferred.Add(match.Groups[1].Value);
        }

        return deferred;
    }
}
