// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Pending approvals of terminal runs close as Rejected because there is nothing left to
/// authorize. The run retains its own terminal status.</summary>
public static class EndedRunApprovals
{
    public const string Reason = "Closed automatically: the run ended before this approval was decided.";
}
