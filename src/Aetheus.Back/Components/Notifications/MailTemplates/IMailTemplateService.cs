// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>The administration of the mail templates (recette R1-75): read, change, restore, send a test.</summary>
public interface IMailTemplateService
{
    /// <summary>Every kind in every language, changed or not.</summary>
    Task<List<MailTemplateSummaryDto>> ListAsync(CancellationToken ct = default);

    /// <summary>The template as it is sent, with the variables of its kind and their samples.</summary>
    Task<MailTemplateDto> GetAsync(MailTemplateKind kind, string language, CancellationToken ct = default);

    /// <summary>Stores the template; a variable its kind does not offer is refused (400).</summary>
    Task<MailTemplateDto> SaveAsync(MailTemplateKind kind, string language, SaveMailTemplateRequest request, string? username, CancellationToken ct = default);

    /// <summary>Goes back to the default template by removing the stored one.</summary>
    Task<MailTemplateDto> ResetAsync(MailTemplateKind kind, string language, CancellationToken ct = default);

    /// <summary>The organization's lines: the kinds an organization may have its own version of
    /// (<see cref="MailTemplateScopes.OrganizationKinds"/>), in every language. 404 for an unknown organization.</summary>
    Task<List<MailTemplateSummaryDto>> ListForOrganizationAsync(int organizationId, CancellationToken ct = default);

    /// <summary>The template the organization's mails are sent with: its own version, else the platform's
    /// (<see cref="MailTemplateDto.IsCustomized"/> false). A kind no organization owns is refused (400).</summary>
    Task<MailTemplateDto> GetForOrganizationAsync(int organizationId, MailTemplateKind kind, string language, CancellationToken ct = default);

    /// <summary>Stores the organization's own version; the platform's is not touched.</summary>
    Task<MailTemplateDto> SaveForOrganizationAsync(
        int organizationId, MailTemplateKind kind, string language, SaveMailTemplateRequest request, string? username, CancellationToken ct = default);

    /// <summary>Removes the organization's own version; it is sent with the platform's again, returned.</summary>
    Task<MailTemplateDto> ResetForOrganizationAsync(int organizationId, MailTemplateKind kind, string language, CancellationToken ct = default);

    /// <summary>
    /// Renders the draft with the samples, in its language and in the caller's colours, and sends it to the
    /// caller's own address through the platform sender; the outcome is the sender's, verbatim.
    /// </summary>
    Task<MailTemplateTestResultDto> SendTestAsync(
        MailTemplateKind kind, string language, SaveMailTemplateRequest request, int? userId, CancellationToken ct = default);
}
