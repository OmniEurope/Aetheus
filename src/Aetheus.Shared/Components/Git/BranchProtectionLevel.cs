// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Git;

/// <summary>
/// Decision D13 (BACK-013): who may move a branch a protection rule matches. A pull request merged
/// through Aetheus moves it at every level; deletion and force-push stay governed by the rule's own
/// switches, enforced by the pre-receive hook.
/// </summary>
public enum BranchProtectionLevel
{
    /// <summary>Anyone with write access may push the branch, and a pipeline may advance it.</summary>
    NotProtected = 0,

    /// <summary>A person goes through a pull request; a pipeline step (advance-branch) may still
    /// advance the branch. The level of every rule that required a pull request before D13.</summary>
    ProtectedExceptPipelines = 1,

    /// <summary>Only a merged pull request moves the branch: direct pushes and pipeline steps are
    /// both refused.</summary>
    StrictlyProtected = 2
}
