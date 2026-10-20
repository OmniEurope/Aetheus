// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>
/// What a mail's template is rendered with: the text and link values, and the block variables (HTML
/// fragments Aetheus writes). Two dictionaries, because OmniMailRenderer refuses a name given as both.
/// </summary>
public sealed record MailTemplateData(IReadOnlyDictionary<string, string?> Values, IReadOnlyDictionary<string, string?> Blocks)
{
    /// <summary>Text and link values only, no block.</summary>
    public static MailTemplateData Text(IReadOnlyDictionary<string, string?> values) =>
        new(values, new Dictionary<string, string?>(StringComparer.Ordinal));
}

/// <summary>A variable a kind offers its template, and what it holds.</summary>
internal sealed record MailTemplateVariable(string Name, MailTemplateVariableKind Kind);
