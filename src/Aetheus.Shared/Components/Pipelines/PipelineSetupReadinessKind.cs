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
    ExternalRepositoryNotInspected
}

/// <summary>
/// Whether a finding stops the run that would follow, or merely deserves attention. Nothing here
/// blocks the wizard itself: creating a pipeline whose adapter is not written yet is legitimate.
/// </summary>
public enum PipelineSetupReadinessSeverity
{
    Warning,
    Blocking
}
