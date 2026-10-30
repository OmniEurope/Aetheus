// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>
/// An organization's own version of a mail template (Administration for organization admins, 2026-10-28):
/// the subject and the HTML body of one <see cref="MailTemplateKind"/> in one language, sent instead of the
/// platform's (<see cref="MailTemplate"/>, else the default) for the mails about that organization's
/// resources. Restoring the platform version removes the row. A table of its own, so the platform rows
/// keep their one-per-kind-and-language rule and the previous release reads them unchanged.
/// </summary>
public class OrganizationMailTemplate
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public MailTemplateKind Kind { get; set; }
    public string Language { get; set; } = MailTemplateLanguages.English;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Organization Organization { get; set; } = null!;
}
