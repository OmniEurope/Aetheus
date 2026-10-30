// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>The stored mail templates, and the two preferences of a recipient a mail is written with.</summary>
public interface IMailTemplateRepository
{
    Task<List<MailTemplate>> ListAsync(CancellationToken ct = default);

    Task<MailTemplate?> FindAsync(MailTemplateKind kind, string language, CancellationToken ct = default);

    /// <summary>Writes the subject and the body of the template, adding its row when it does not exist.</summary>
    Task<MailTemplate> UpsertAsync(MailTemplateKind kind, string language, string subject, string body, string? updatedBy, CancellationToken ct = default);

    /// <summary>Removes the template's row; false when there was none.</summary>
    Task<bool> DeleteAsync(MailTemplateKind kind, string language, CancellationToken ct = default);

    /// <summary>The organization's own versions (Administration for organization admins).</summary>
    Task<List<OrganizationMailTemplate>> ListForOrganizationAsync(int organizationId, CancellationToken ct = default);

    Task<OrganizationMailTemplate?> FindForOrganizationAsync(int organizationId, MailTemplateKind kind, string language, CancellationToken ct = default);

    /// <summary>Writes the organization's own version, adding its row when it does not exist.</summary>
    Task<OrganizationMailTemplate> UpsertForOrganizationAsync(
        int organizationId, MailTemplateKind kind, string language, string subject, string body, string? updatedBy, CancellationToken ct = default);

    /// <summary>Removes the organization's own version; false when there was none.</summary>
    Task<bool> DeleteForOrganizationAsync(int organizationId, MailTemplateKind kind, string language, CancellationToken ct = default);

    Task<bool> OrganizationExistsAsync(int organizationId, CancellationToken ct = default);

    /// <summary>
    /// The language and appearance preference values (JSON) of the account with that address, an active
    /// account first; both null when no account has it.
    /// </summary>
    Task<(string? LanguageJson, string? AppearanceJson)> FindRecipientPreferencesAsync(string email, CancellationToken ct = default);

    /// <summary>The address of a user, null when the account has none.</summary>
    Task<string?> FindUserEmailAsync(int userId, CancellationToken ct = default);
}
