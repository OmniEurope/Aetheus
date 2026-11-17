// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// What the pre-creation readiness check found missing. Each value is a distinct cause with its own
/// remediation, so the wizard can send the user to the page that fixes it instead of printing prose.
/// </summary>
public enum PipelineSetupReadinessKind
{
    /// <summary>The project has no internal repository, so nothing can be inspected or built.</summary>
    NoRepository,

    /// <summary>The branch the pipelines will read has no commit yet, so no adapter can exist.</summary>
    EmptyBranch,

    /// <summary>Adapter scripts the selected templates invoke are absent from the branch. Items are paths.</summary>
    MissingAdapterScripts,

    /// <summary>No server at all is configured, so no stage could ever be dispatched.</summary>
    NoRunnerConfigured,

    /// <summary>A deployment stage was selected but no deployment-capable server exists.</summary>
    NoDeployRunnerConfigured,

    /// <summary>Templates name environments that do not exist. Items are environment names.</summary>
    MissingEnvironments,

    /// <summary>A selected template declares `requires.libraries` entries this installation does not
    /// have. Items are library names, which are exactly what has to be created before launching.</summary>
    MissingRequiredLibraries,

    /// <summary>A selected template declares `requires.vaults` entries this installation does not
    /// have. Items are vault names.</summary>
    MissingRequiredVaults,

    /// <summary>A selected template declares `requires.capabilities` this installation cannot satisfy
    /// (or that this control plane cannot verify). Items are capability names.</summary>
    MissingRequiredCapabilities,

    /// <summary>A platform scanner of a selected template reads its configuration from the repository
    /// and the branch does not carry it (Gitleaks and <c>.gitleaks.toml</c>). Items are paths from the
    /// repository root.</summary>
    MissingScannerConfiguration,

    /// <summary>The project's repository is external and Aetheus holds no readable copy of it (a
    /// plain repository address, or a mirror not synchronized yet), so the files the templates need
    /// cannot be checked from here. A warning, never "missing": nobody saw them absent. Items are
    /// the paths to check in that repository.</summary>
    ExternalRepositoryNotInspected,

    /// <summary>A variable library the pipelines read holds an empty value for a variable they use
    /// (<c>$(NAME)</c> without a default): a copied library is created with empty values. A run would stop
    /// on it. Items are variable names.</summary>
    EmptyLibraryValues,

    /// <summary>Same as <see cref="EmptyLibraryValues"/> for a vault secret. Items are secret names.</summary>
    EmptyVaultSecrets,

    /// <summary>The branch holds no .NET solution (<c>*.sln</c>, <c>*.slnx</c>) and a selected pipeline
    /// builds with the template's default .NET commands, which stop on it. Items are the variables that
    /// replace those commands (<c>APPLICATION_CI_COMMANDS</c>, <c>APPLICATION_QUALITY_COMMANDS</c>).</summary>
    NoDotNetSolution,

    /// <summary>The branch holds no .NET test project a QA can run and the QA pipeline uses the template's
    /// default commands, which fail rather than validate nothing. Items are the variables that replace them.</summary>
    NoDotNetTestProject,

    /// <summary>The branch holds no Compose file at its root and the production deployment uses the
    /// template's default commands, which need one. Items are the file names looked for.</summary>
    NoComposeFile,

    /// <summary>No enrolled runner reports Docker and the production deployment uses the template's default
    /// commands, which run Docker Compose.</summary>
    NoDockerRunner,

    /// <summary>R-9: the pipelines read variables that no library, vault, declared variable or run parameter
    /// defines (a <c>$(NAME)</c> or an environment sample's <c>#{NAME}#</c>): a run stops on them. Items are
    /// variable names.</summary>
    UndefinedVariables,

    /// <summary>R-36: a pipeline starts another on success (<c>on_success:</c>), and the launch that follows is given no
    /// <c>deployServer</c> while the target requires one without a default and the project's servers do not make the choice
    /// unique: the follow-up will be refused. A warning (the upstream run itself is not at risk). Items are
    /// <c>upstream &gt; target</c> pipeline names.</summary>
    ChainedDeployServerMissing,

    /// <summary>R-36: a pipeline starts another on success (<c>on_success:</c>) and the target declares another required
    /// parameter with no default that the launch that follows does not supply (it gives the release as
    /// <c>candidateVersion</c> and nothing else): the follow-up will be refused. A warning. Items are
    /// <c>upstream &gt; target &gt; parameter</c> names.</summary>
    ChainedRequiredParameterMissing,

    /// <summary>R-30: the branch holds a .NET repository with no <c>global.json</c> at its root, so no SDK version is pinned: the
    /// pipelines' toolchain step installs the SDK channel of the projects' <c>TargetFramework</c> instead of an exact one. A
    /// warning (nothing fails). Items are the file to add (<c>global.json</c>).</summary>
    NoGlobalJson
}

/// <summary>
/// Whether a finding stops the run that would follow, or merely deserves attention. Nothing here
/// blocks the wizard itself: creating a pipeline whose adapter is not written yet is legitimate.
/// </summary>
public enum PipelineSetupReadinessSeverity
{
    Warning,
    Blocking,

    /// <summary>R-21: a mistake of the setup that the user must fix, shown as an error, which does not stop the run the
    /// finding is read from (a follow-up that will be refused, the upstream run itself is not at risk). Not
    /// <see cref="Blocking"/>: it never holds back a launch or the wizard.</summary>
    Error
}
