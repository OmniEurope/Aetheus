// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// The stable codes the engine stores in <see cref="PipelineStepRunDto.SkippedReason"/> when it finishes a step
/// without running anything because the work is already done. A code, never a sentence in one language: the front
/// localizes it. Any other value of that field (an <c>allow_missing</c> restore, for instance) is an English detail
/// the front shows as it is.
/// </summary>
public static class PipelineStepSkipReasons
{
    /// <summary>Recette R-39: a step that only declares <c>checkout: true</c> has no command of its own. The clone
    /// of the exact revision is the run preparation's (<c>System:Prepare</c>), done before any stage, so the step is
    /// finished without a task and said so, instead of reporting a success for a checkout it did not make.</summary>
    public const string CheckoutDoneByPreparation = "checkout-done-by-preparation";
}
