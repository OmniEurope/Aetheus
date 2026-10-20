// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.MailTemplates;

/// <summary>The templates of the platform mails (recette R1-75), administrators only.</summary>
public sealed class MailTemplatesApi(HttpClient http) : ApiClientBase(http)
{
    public async Task<List<MailTemplateSummaryDto>> ListAsync(CancellationToken ct = default) =>
        await GetJsonAsync<List<MailTemplateSummaryDto>>("api/mail-templates", ct) ?? [];

    public Task<MailTemplateDto?> GetAsync(MailTemplateKind kind, string language, CancellationToken ct = default) =>
        GetJsonAsync<MailTemplateDto>(Url(kind, language), ct);

    /// <summary>Saves the template; a 400 carries the server's reason (a variable the mail does not offer).</summary>
    public Task<ApiOutcome<MailTemplateDto, ApiError>> SaveAsync(
        MailTemplateKind kind, string language, SaveMailTemplateRequest request, CancellationToken ct = default) =>
        PutForOutcomeAsync<MailTemplateDto, ApiError>(Url(kind, language), request, ct);

    /// <summary>Restores the default template and returns it.</summary>
    public Task<ApiOutcome<MailTemplateDto, ApiError>> ResetAsync(MailTemplateKind kind, string language, CancellationToken ct = default) =>
        SendForOutcomeAsync<MailTemplateDto, ApiError>(HttpMethod.Delete, Url(kind, language), null, ct);

    /// <summary>Sends the draft, rendered with the samples, to the signed-in administrator's own address.</summary>
    public Task<ApiOutcome<MailTemplateTestResultDto, ApiError>> SendTestAsync(
        MailTemplateKind kind, string language, SaveMailTemplateRequest request, CancellationToken ct = default) =>
        PostForOutcomeAsync<MailTemplateTestResultDto, ApiError>(Url(kind, language) + "/test", request, ct);

    private static string Url(MailTemplateKind kind, string language) =>
        $"api/mail-templates/{kind}/{Uri.EscapeDataString(language)}";
}
