// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;
using OmniEurope.Blazor.Components;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

public sealed class MailTemplateService(
    IMailTemplateRepository repository,
    IMailTemplateRenderer renderer,
    IEmailSender sender,
    IAuditService audit) : IMailTemplateService
{
    internal const string NoAddressMessage =
        "Your account has no e-mail address: an administrator sets it in the user administration; nothing was sent.";

    public async Task<List<MailTemplateSummaryDto>> ListAsync(CancellationToken ct = default)
    {
        var stored = await repository.ListAsync(ct).ConfigureAwait(false);
        return [.. from kind in MailTemplateCatalog.Kinds
                   from language in MailTemplateLanguages.All
                   let row = stored.FirstOrDefault(template => template.Kind == kind && template.Language == language)
                   select new MailTemplateSummaryDto
                   {
                       Kind = kind,
                       Language = language,
                       IsCustomized = row is not null,
                       UpdatedAt = row?.UpdatedAt,
                       UpdatedBy = row?.UpdatedBy
                   }];
    }

    public async Task<MailTemplateDto> GetAsync(MailTemplateKind kind, string language, CancellationToken ct = default)
    {
        EnsureKnown(kind, language);
        return ToDto(kind, language, await repository.FindAsync(kind, language, ct).ConfigureAwait(false));
    }

    public async Task<MailTemplateDto> SaveAsync(
        MailTemplateKind kind, string language, SaveMailTemplateRequest request, string? username, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureKnown(kind, language);
        EnsureOfferedVariables(kind, request);
        var row = await repository.UpsertAsync(kind, language, request.Subject.Trim(), request.Body, username, ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "MailTemplate", row.Id, $"{kind} ({language})", ct).ConfigureAwait(false);
        return ToDto(kind, language, row);
    }

    public async Task<MailTemplateDto> ResetAsync(MailTemplateKind kind, string language, CancellationToken ct = default)
    {
        EnsureKnown(kind, language);
        if (await repository.DeleteAsync(kind, language, ct).ConfigureAwait(false))
            await audit.LogAsync("Reset", "MailTemplate", null, $"{kind} ({language})", ct).ConfigureAwait(false);
        return ToDto(kind, language, null);
    }

    public async Task<List<MailTemplateSummaryDto>> ListForOrganizationAsync(int organizationId, CancellationToken ct = default)
    {
        await EnsureOrganizationAsync(organizationId, ct).ConfigureAwait(false);
        var own = await repository.ListForOrganizationAsync(organizationId, ct).ConfigureAwait(false);
        return [.. from kind in MailTemplateScopes.OrganizationKinds
                   from language in MailTemplateLanguages.All
                   let row = own.FirstOrDefault(template => template.Kind == kind && template.Language == language)
                   select new MailTemplateSummaryDto
                   {
                       Kind = kind,
                       Language = language,
                       IsCustomized = row is not null,
                       UpdatedAt = row?.UpdatedAt,
                       UpdatedBy = row?.UpdatedBy,
                       OrganizationId = organizationId
                   }];
    }

    public async Task<MailTemplateDto> GetForOrganizationAsync(int organizationId, MailTemplateKind kind, string language, CancellationToken ct = default)
    {
        await EnsureOrganizationKindAsync(organizationId, kind, language, ct).ConfigureAwait(false);
        var own = await repository.FindForOrganizationAsync(organizationId, kind, language, ct).ConfigureAwait(false);
        return await ForOrganizationAsync(organizationId, kind, language, own, ct).ConfigureAwait(false);
    }

    public async Task<MailTemplateDto> SaveForOrganizationAsync(
        int organizationId, MailTemplateKind kind, string language, SaveMailTemplateRequest request, string? username, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureOrganizationKindAsync(organizationId, kind, language, ct).ConfigureAwait(false);
        EnsureOfferedVariables(kind, request);
        var row = await repository.UpsertForOrganizationAsync(
            organizationId, kind, language, request.Subject.Trim(), request.Body, username, ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "OrganizationMailTemplate", row.Id, $"{kind} ({language}) of organization {organizationId}", ct).ConfigureAwait(false);
        return await ForOrganizationAsync(organizationId, kind, language, row, ct).ConfigureAwait(false);
    }

    public async Task<MailTemplateDto> ResetForOrganizationAsync(int organizationId, MailTemplateKind kind, string language, CancellationToken ct = default)
    {
        await EnsureOrganizationKindAsync(organizationId, kind, language, ct).ConfigureAwait(false);
        if (await repository.DeleteForOrganizationAsync(organizationId, kind, language, ct).ConfigureAwait(false))
            await audit.LogAsync("Reset", "OrganizationMailTemplate", null, $"{kind} ({language}) of organization {organizationId}", ct).ConfigureAwait(false);
        return await ForOrganizationAsync(organizationId, kind, language, null, ct).ConfigureAwait(false);
    }

    /// <summary>The organization's version when it has one, else the platform's as it is sent.</summary>
    private async Task<MailTemplateDto> ForOrganizationAsync(
        int organizationId, MailTemplateKind kind, string language, OrganizationMailTemplate? own, CancellationToken ct)
    {
        if (own is null)
        {
            var platform = ToDto(kind, language, await repository.FindAsync(kind, language, ct).ConfigureAwait(false));
            return platform with { IsCustomized = false, UpdatedAt = null, UpdatedBy = null, OrganizationId = organizationId };
        }
        var stored = new MailTemplate
        {
            Kind = kind,
            Language = language,
            Subject = own.Subject,
            Body = own.Body,
            UpdatedAt = own.UpdatedAt,
            UpdatedBy = own.UpdatedBy
        };
        return ToDto(kind, language, stored) with { OrganizationId = organizationId };
    }

    private async Task EnsureOrganizationAsync(int organizationId, CancellationToken ct)
    {
        if (!await repository.OrganizationExistsAsync(organizationId, ct).ConfigureAwait(false))
            throw new NotFoundException($"Organization {organizationId} not found.");
    }

    private async Task EnsureOrganizationKindAsync(int organizationId, MailTemplateKind kind, string language, CancellationToken ct)
    {
        EnsureKnown(kind, language);
        if (!MailTemplateScopes.IsOrganizationKind(kind))
            throw new BadRequestException($"The '{kind}' mail concerns no organization: only the platform's version exists.");
        await EnsureOrganizationAsync(organizationId, ct).ConfigureAwait(false);
    }

    public async Task<MailTemplateTestResultDto> SendTestAsync(
        MailTemplateKind kind, string language, SaveMailTemplateRequest request, int? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureKnown(kind, language);
        EnsureOfferedVariables(kind, request);
        var to = userId is { } id ? await repository.FindUserEmailAsync(id, ct).ConfigureAwait(false) : null;
        if (string.IsNullOrWhiteSpace(to))
            return new MailTemplateTestResultDto { Status = NotificationTestStatus.NotConfigured, Message = NoAddressMessage };

        // The caller's own colours, in the language of the template being written.
        var style = await renderer.ResolveRecipientAsync(to, ct).ConfigureAwait(false) with { Language = language };
        var mail = MailTemplateRenderer.Render(request.Subject, request.Body, MailTemplateCatalog.Samples(kind, language), style);
        var result = await sender.SendAsync(new EmailMessage(to.Trim(), mail.Subject, mail.Text, mail.Html), ct).ConfigureAwait(false);
        if (result.Status == EmailSendStatus.Sent)
            await audit.LogAsync("Tested", "MailTemplate", null, $"{kind} ({language})", ct).ConfigureAwait(false);
        return new MailTemplateTestResultDto
        {
            Status = result.Status switch
            {
                EmailSendStatus.Sent => NotificationTestStatus.Sent,
                EmailSendStatus.NotConfigured => NotificationTestStatus.NotConfigured,
                _ => NotificationTestStatus.Failed
            },
            To = to.Trim(),
            Message = result.Message
        };
    }

    private static MailTemplateDto ToDto(MailTemplateKind kind, string language, MailTemplate? row)
    {
        var (subject, body) = row is null ? MailTemplateDefaults.For(kind, language) : (row.Subject, row.Body);
        var samples = MailTemplateCatalog.Samples(kind, language);
        return new MailTemplateDto
        {
            Kind = kind,
            Language = language,
            Subject = subject,
            Body = body,
            IsCustomized = row is not null,
            UpdatedAt = row?.UpdatedAt,
            UpdatedBy = row?.UpdatedBy,
            Variables = [.. MailTemplateCatalog.Variables(kind).Select(variable => new MailTemplateVariableDto
            {
                Name = variable.Name,
                Kind = variable.Kind,
                SampleValue = (variable.Kind == MailTemplateVariableKind.Block ? samples.Blocks : samples.Values)
                    .GetValueOrDefault(variable.Name) ?? string.Empty
            })]
        };
    }

    private static void EnsureKnown(MailTemplateKind kind, string language)
    {
        if (!Enum.IsDefined(kind))
            throw new NotFoundException($"Unknown mail template kind '{kind}'.");
        if (!MailTemplateLanguages.IsSupported(language))
            throw new BadRequestException($"A mail template is written in one of: {string.Join(", ", MailTemplateLanguages.All)}.");
    }

    /// <summary>A variable the kind does not offer would be sent empty to every recipient: refused before it is stored.</summary>
    private static void EnsureOfferedVariables(MailTemplateKind kind, SaveMailTemplateRequest request)
    {
        var offered = MailTemplateCatalog.VariableNames(kind);
        var unknown = OmniMailRenderer.FindVariables(request.Subject)
            .Concat(OmniMailRenderer.FindVariables(request.Body))
            .Distinct(StringComparer.Ordinal)
            .Where(name => !offered.Contains(name, StringComparer.Ordinal))
            .ToList();
        if (unknown.Count > 0)
            throw new BadRequestException(
                $"This mail does not offer the variable(s) {string.Join(", ", unknown.Select(name => "{{" + name + "}}"))}; it offers {string.Join(", ", offered.Select(name => "{{" + name + "}}"))}.");
    }
}
