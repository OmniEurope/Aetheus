// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>What one read of a run's steps says about its stages: the stages that completed, and the stages
/// that are settled (every step terminal), whatever the way each ended.</summary>
/// <param name="Completed">Stages whose steps are all terminal and each passed, was cancelled or may continue on
/// error: the dependencies ordinary stages wait for.</param>
/// <param name="Terminal">Stages whose steps have all reached a terminal status, hard failures included: what
/// releases <c>always()</c>/<c>failed()</c> handlers and the system cleanup without treating a failed stage as a
/// successful dependency of an ordinary one. Always a superset of <paramref name="Completed"/>.</param>
public sealed record PipelineStageProgress(List<string> Completed, List<string> Terminal);
