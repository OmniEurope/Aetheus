// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary> Who asked for an approval. Both can apply to the same run and are decided separately:
/// approving one never satisfies the other. </summary>
public enum ApprovalScope
{
    /// <summary>The target environment's own policy (<c>RequireApproval</c>): one decision per run
    /// and environment, whatever the pipeline says.</summary>
    Environment = 0,

    /// <summary>The pipeline definition itself: a stage declaring <c>approval_timeout_minutes</c>
    /// asks before it runs, with or without an environment.</summary>
    Pipeline = 1
}
