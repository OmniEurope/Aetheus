// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>The pipeline prefix a project name proposes: lowercase, every character that is neither a
/// letter nor a digit turned into a hyphen, runs of hyphens collapsed and trimmed. Shared by the setup
/// wizard and the project copy flow, so neither depends on the other for it.</summary>
internal static class PipelinePrefix
{
    internal static string FromName(string value)
    {
        var slug = new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }
}
