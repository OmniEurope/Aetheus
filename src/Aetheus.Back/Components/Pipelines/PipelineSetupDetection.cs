// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Which of the generic templates' default commands the selected pipelines will run, and whether the
/// repository (and the fleet) can satisfy them. A default command needs a .NET solution (CI, quality),
/// a .NET test project (QA), a Compose file and Docker (production deployment); when it is missing the
/// run fails at its first execution, so the finding is raised before the pipelines are created.
///
/// A pipeline that declares its own commands (<c>APPLICATION_*_COMMANDS</c> in its own YAML) does not
/// use the default: nothing is asked about it.
/// </summary>
internal sealed class PipelineSetupDetection(IPipelineRepository pipelines, IGitLightService git, ILogger logger)
{
    internal const string CiCommands = "APPLICATION_CI_COMMANDS";
    internal const string QualityCommands = "APPLICATION_QUALITY_COMMANDS";
    internal const string QaCommands = "APPLICATION_QA_COMMANDS";
    internal const string DeployProdCommands = "APPLICATION_DEPLOY_PROD_COMMANDS";

    /// <summary>The command variables the selected definitions carry from their template and do not
    /// replace in the pipeline itself: the ones whose default will run.</summary>
    public static HashSet<string> DefaultCommands(
        IEnumerable<PipelineYamlDefinition> definitions, IReadOnlySet<string> overridden) =>
        [.. definitions
            .SelectMany(definition => definition.Variables.Keys)
            .Where(key => key is CiCommands or QualityCommands or QaCommands or DeployProdCommands
                && !overridden.Contains(key))];

    /// <summary>Reads what the default commands in <paramref name="defaults"/> need and reports what is
    /// missing. <paramref name="repositoryId"/> and <paramref name="branch"/> are null when the repository
    /// could not be read: the file answers stay null and nothing is claimed about them.</summary>
    public async Task<PipelineSetupDetectionDto> DetectAsync(
        HashSet<string> defaults,
        int? repositoryId,
        string? branch,
        int? organizationId,
        List<PipelineSetupReadinessCheckDto> checks,
        CancellationToken ct)
    {
        var needsSolution = defaults.Contains(CiCommands) || defaults.Contains(QualityCommands);
        var needsTests = defaults.Contains(QaCommands);
        var needsCompose = defaults.Contains(DeployProdCommands);

        bool? solution = null, tests = null, compose = null, docker = null;
        if (repositoryId is { } id && branch is not null)
        {
            var probe = new PipelineSetupRepositoryProbe(git, logger, id, branch);
            if (needsSolution) solution = await probe.HasSolutionAsync(ct).ConfigureAwait(false);
            if (needsTests) tests = await probe.HasRunnableTestProjectAsync(ct).ConfigureAwait(false);
            if (needsCompose) compose = await probe.HasComposeFileAsync(ct).ConfigureAwait(false);
        }

        if (needsCompose)
            docker = await pipelines.HasRunnerWithDockerAsync(organizationId, ct).ConfigureAwait(false);

        if (solution == false)
            Add(checks, PipelineSetupReadinessKind.NoDotNetSolution,
                [.. new[] { CiCommands, QualityCommands }.Where(defaults.Contains)]);
        if (tests == false)
            Add(checks, PipelineSetupReadinessKind.NoDotNetTestProject, [QaCommands]);
        if (compose == false)
            Add(checks, PipelineSetupReadinessKind.NoComposeFile, [.. PipelineSetupRepositoryProbe.ComposeFileNames]);
        if (docker == false)
            Add(checks, PipelineSetupReadinessKind.NoDockerRunner, [DeployProdCommands]);

        return new PipelineSetupDetectionDto
        {
            HasDotNetSolution = solution,
            HasDotNetTestProject = tests,
            HasComposeFile = compose,
            HasDockerRunner = docker
        };
    }

    private static void Add(
        List<PipelineSetupReadinessCheckDto> checks, PipelineSetupReadinessKind kind, List<string> items) =>
        checks.Add(new PipelineSetupReadinessCheckDto
        {
            Kind = kind,
            Severity = PipelineSetupReadinessSeverity.Blocking,
            Items = items
        });
}
