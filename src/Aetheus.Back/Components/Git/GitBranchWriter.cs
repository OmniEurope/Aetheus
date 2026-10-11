// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Git;

/// <summary>The path a branch update comes through, as decision D13 tells them apart.</summary>
public enum GitBranchWriter
{
    /// <summary>A person pushing over smart HTTP.</summary>
    HumanPush,

    /// <summary>The merge of a pull request through Aetheus.</summary>
    PullRequestMerge,

    /// <summary>A server-side pipeline write, the <c>advance-branch</c> step.</summary>
    PipelineAdvance
}
