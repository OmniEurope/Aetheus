// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>A picked release fills the sole required string parameter without a default. Sharing
/// this rule between dialogs and fields avoids guessing when multiple targets exist. The production server a
/// deployment also asks for (<see cref="RunParameterServerOffer.ParameterName"/>) is a server name, never a version,
/// so it does not count as a competing target (recette R-76).</summary>
internal static class RunParameterTargets
{
    internal static string? ReleaseFillTarget(IEnumerable<PipelineRunParameterDto> parameters) =>
        parameters
            .Where(p => p.Required
                && string.IsNullOrWhiteSpace(p.Default)
                && p.Name != RunParameterServerOffer.ParameterName
                && (string.IsNullOrEmpty(p.Type) || p.Type.Equals("string", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList() is [var single] ? single : null;
}
