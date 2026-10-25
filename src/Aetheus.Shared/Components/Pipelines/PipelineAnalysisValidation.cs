// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Pipelines;

internal static class PipelineAnalysisValidation
{
    internal static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) return false;
        if (!char.IsLetterOrDigit(key[0]) || !char.IsLetterOrDigit(key[^1])) return false;
        return key.All(character =>
            char.IsAsciiLetterLower(character) || char.IsDigit(character) || character is '.' or '-');
    }
}
