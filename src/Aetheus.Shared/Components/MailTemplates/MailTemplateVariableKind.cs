// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.MailTemplates;

/// <summary>
/// What a template variable holds, which decides how the editor offers it: a text, an address an action
/// button may point to, or an HTML fragment Aetheus writes and the template places as a block.
/// </summary>
public enum MailTemplateVariableKind
{
    Text = 0,
    Link = 1,
    Block = 2
}
