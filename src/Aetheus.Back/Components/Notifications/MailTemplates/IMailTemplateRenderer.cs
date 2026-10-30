// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>
/// Renders a platform mail from its template (recette R1-75) in its recipient's language and colours
/// (recette R1-74). The senders keep their own delivery and outcome reporting; this only writes the mail.
/// </summary>
public interface IMailTemplateRenderer
{
    /// <summary>The language and theme of the account with that address; the platform default when none has it.</summary>
    Task<MailRecipientStyle> ResolveRecipientAsync(string? email, CancellationToken ct = default);

    /// <summary>As above, but an account with no language preference (or no account at all) is written to in
    /// <paramref name="defaultLanguage"/> instead of English: the language an administrator chose for the mail (recette R1-8).</summary>
    Task<MailRecipientStyle> ResolveRecipientAsync(string? email, string defaultLanguage, CancellationToken ct = default);

    /// <summary>
    /// Renders the template of the kind in the style's language (the administrator's version, else the
    /// default one) with the values and the blocks (HTML fragments placed by their token). A variable the
    /// template uses but no value carries renders nothing and is listed in <see cref="OmniMailRenderResult.MissingVariables"/>.
    /// </summary>
    Task<OmniMailRenderResult> RenderAsync(
        MailTemplateKind kind, MailTemplateData data, MailRecipientStyle style, CancellationToken ct = default);

    /// <summary>
    /// As above, for a mail about a resource of <paramref name="organizationId"/>: the organization's own
    /// version of the kind comes first, then the platform's, then the default. A null organization is the
    /// form above.
    /// </summary>
    Task<OmniMailRenderResult> RenderAsync(
        MailTemplateKind kind, MailTemplateData data, MailRecipientStyle style, int? organizationId, CancellationToken ct = default);
}
