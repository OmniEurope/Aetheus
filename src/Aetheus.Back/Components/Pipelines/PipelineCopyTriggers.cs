// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// A copied pipeline must not start on its own: a copy of a nightly or webhook pipeline would run in
/// the new project (against its repository and its libraries) before anyone has looked at it. The
/// trigger of a pipeline is its own top-level <c>trigger:</c> key (<c>PipelineService</c> reads nothing
/// else), so the copy is written with <c>trigger: manual</c>. The <c>schedule:</c>, <c>branches:</c> and
/// <c>paths_ignore:</c> keys stay in the definition: setting <c>trigger:</c> back is all it takes to
/// enable the copy.
/// </summary>
internal static partial class PipelineCopyTriggers
{
    [GeneratedRegex(
        @"^(?<lead>trigger[ \t]*:[ \t]*)(?<quote>[""']?)(?<value>webhook|schedule)\k<quote>[ \t]*(?:#[^\r\n]*)?(?=\r?$)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TriggerLine { get; }

    /// <summary>The definition with a <c>webhook</c> or <c>schedule</c> trigger set to manual, and the
    /// trigger it had (lowercase); null, and the definition unchanged, when it had none.</summary>
    internal static (string Yaml, string? Original) Disable(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        string? original = null;
        var disabled = TriggerLine.Replace(yaml, match =>
        {
            original ??= match.Groups["value"].Value.ToLowerInvariant();
            return $"{match.Groups["lead"].Value}manual # copy: was '{original}', set it back to enable";
        }, 1);
        return (disabled, original);
    }

    /// <summary>Whether the definition would still run on its own: it parses with a webhook or
    /// schedule trigger. A definition that does not parse is not "active": validation refuses it.</summary>
    internal static bool IsActive(string yaml) =>
        YamlParsingHelper.ParseAndValidate(yaml) is { Trigger: { } trigger }
        && (string.Equals(trigger, "webhook", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, "schedule", StringComparison.OrdinalIgnoreCase));
}
