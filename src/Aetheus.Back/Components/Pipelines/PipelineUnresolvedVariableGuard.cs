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
        "BUILD_BUILDID", "BUILD_BUILDNUMBER", "BUILD_PIPELINE_RUNNUMBER", "BUILD_RELEASENUMBER", "BUILD_RELEASESERIES", "BUILD_PIPELINEID",
        "BUILD_PIPELINENAME", "BUILD_RUN_PORT", "BUILD_TRIGGEREDBY", "BUILD_PROJECTID",
        "BUILD_PROJECTNAME", "BUILD_SOURCEVERSION", "BUILD_SOURCEBRANCH", "BUILD_REPOSITORY_URI",
        "REPOSITORY_URL", "DEFAULT_BRANCH", "WORKSPACE", "PROJECT_TYPE", "UPSTREAM_RELEASE",
        "SYSTEM_DATE", "SYSTEM_DATETIME", "SYSTEM_TIMESTAMP", "SYSTEM_STAGENAME",
        "AGENT_NAME", "AGENT_HOSTNAME", "AGENT_OS", "AGENT_PLATFORM", "AGENT_ID",
        "AETHEUS_VERSION", "CI",
        // Set when the source block selects another repository for the workspace.
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
        foreach (var (text, origin) in StepTexts(definition))
            Collect(text, origin, resolved, deferred, failures);

        return failures;
    }

    /// <summary>Every text of a step the control plane substitutes at dispatch, with the step it
    /// belongs to.</summary>
    private static IEnumerable<(string? Text, string Origin)> StepTexts(PipelineYamlDefinition definition)
    {
        foreach (var stage in YamlParsingHelper.FlattenJobs(definition))
            foreach (var step in stage.Steps)
            {
                var origin = $"step '{step.Name}'";
                yield return (step.Shell, origin);
                yield return (step.WorkingDirectory, origin);
                yield return (step.EnvTemplate, origin);
                // The rendered vhost CONTENT is already fail-closed (RenderStrict throws on an
                // undefined #{TOKEN}#), but the destination file name is not: a missing DOCS_DOMAIN
                // would install a vhost literally called "$(DOCS_DOMAIN).conf".
                foreach (var (target, source) in step.ConfigFiles)
                {
                    yield return (target, origin);
                    yield return (source, origin);
                }
                // The pipeline a step triggers or restores from is substituted at dispatch too. The
                // launch preflight judges the expanded name's existence and leaves a reference only
                // known later to the step; a reference nothing can provide is refused here instead.
                yield return (step.Pipeline, origin);
                yield return (step.ArtifactSourcePipeline, origin);
            }
    }

    /// <summary>
    /// Refuses a run whose library or vault supplies an EMPTY value it needs. A copied library or vault
    /// is created with every value empty, and an empty string is a resolved value, so nothing else
    /// stops the run: it would build with <c>""</c> where a host name or a password belongs.
    /// <list type="bullet">
    /// <item>A vault secret is never legitimately empty (the API refuses one), so any empty secret of a
    /// vault the pipeline reads stops the run.</item>
    /// <item>A library entry may be empty on purpose, so it stops the run only when the pipeline uses it
    /// as <c>$(NAME)</c> without a default. <c>$(NAME:-)</c> states that empty is acceptable.</item>
    /// </list>
    /// </summary>
    internal static void ValidateNoEmptyValues(
        PipelineYamlDefinition definition,
        IReadOnlyDictionary<string, string> resolved,
        IReadOnlySet<string> secretKeys,
        IReadOnlySet<string> emptyLibraryKeys)
    {
        ArgumentNullException.ThrowIfNull(definition);
        // Still empty in the variables of the run: a value supplied for this run replaces the entry.
        var stillEmpty = emptyLibraryKeys
            .Where(key => resolved.TryGetValue(key, out var value) && value.Length == 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var emptyLibrary = FindConsumedEmptyLibraryKeys(definition, stillEmpty);
        var emptySecrets = secretKeys
            .Where(key => resolved.TryGetValue(key, out var value) && value.Length == 0)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (emptyLibrary.Count == 0 && emptySecrets.Count == 0) return;

        var parts = new List<string>();
        if (emptyLibrary.Count > 0)
            parts.Add("variable library entries " + string.Join(", ", emptyLibrary.Select(entry =>
                $"'{entry.Key}' (used by {string.Join(", ", entry.Value)})")));
        if (emptySecrets.Count > 0)
            parts.Add("vault secrets " + string.Join(", ", emptySecrets.Select(key => $"'{key}'")));
        throw new BadRequestException(
            "Empty value(s) must be filled in before this pipeline can run: " + string.Join("; ", parts)
            + ". A copied library or vault is created with empty values: enter them in the Variable Library "
            + "or the Vault (an entry that may stay empty is used as $(NAME:-) in the pipeline).");
    }

    /// <summary>The library entries among <paramref name="emptyLibraryKeys"/> that the pipeline uses as
    /// <c>$(NAME)</c> without a default and that are still empty in the variables of the run, mapped to
    /// what uses them: the pipeline's own variables, its stages' variables and its steps.</summary>
    internal static SortedDictionary<string, SortedSet<string>> FindConsumedEmptyLibraryKeys(
        PipelineYamlDefinition definition,
        IReadOnlySet<string> emptyLibraryKeys)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var consumed = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (emptyLibraryKeys.Count == 0) return consumed;

        var texts = definition.Variables.Select(pair => ((string?)pair.Value, $"variable {pair.Key}"))
            .Concat(YamlParsingHelper.FlattenJobs(definition).SelectMany(stage => stage.Variables)
                .Select(pair => ((string?)pair.Value, $"variable {pair.Key}")))
            .Concat(StepTexts(definition));
        foreach (var (text, origin) in texts)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains("$(", StringComparison.Ordinal)) continue;
            foreach (Match match in VariableReference().Matches(text))
            {
                var name = match.Groups[1].Value;
                if (match.Groups[2].Success || !emptyLibraryKeys.Contains(name)) continue;
                if (!consumed.TryGetValue(name, out var usedBy))
                    consumed[name] = usedBy = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                usedBy.Add(origin);
            }
        }
        return consumed;
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
            // A catalog function publishes the outputs its descriptor declares.
            if (PipelineFunctionCatalog.Find(step.Function) is { } function && string.Equals(step.Type, PipelineFunctionCatalog.StepType, StringComparison.OrdinalIgnoreCase))
                deferred.UnionWith(function.Outputs);
            if (string.IsNullOrEmpty(step.Shell)) continue;
            foreach (Match match in PublishedName().Matches(step.Shell))
                deferred.Add(match.Groups[1].Value);
        }

        return deferred;
    }
}
