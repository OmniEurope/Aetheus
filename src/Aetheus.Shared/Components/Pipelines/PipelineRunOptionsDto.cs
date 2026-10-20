// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// The launch options the platform offers for a manual run. Read by the run dialog, which shows an
/// option only when the platform allows it; the backend refuses it on its own as well.
/// </summary>
public sealed record PipelineRunOptionsDto
{
    /// <summary>Whether a manual run may be launched in "run to end" mode
    /// (<see cref="PipelineRunRequest.RunToEnd"/>).</summary>
    public bool RunToEndAllowed { get; init; }
}
