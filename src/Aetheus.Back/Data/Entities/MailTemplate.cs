// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>
/// A mail template an administrator changed (recette R1-75): the subject and the HTML body of one
/// <see cref="MailTemplateKind"/> in one language. A kind and language without a row is sent with its
/// default template; restoring the default removes the row.
/// </summary>
public class MailTemplate
{
    public int Id { get; set; }
    public MailTemplateKind Kind { get; set; }
    public string Language { get; set; } = MailTemplateLanguages.English;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
