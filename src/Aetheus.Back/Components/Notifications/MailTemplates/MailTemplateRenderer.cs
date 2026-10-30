// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

public sealed class MailTemplateRenderer(IMailTemplateRepository repository, ILogger<MailTemplateRenderer> logger) : IMailTemplateRenderer
{
    public Task<MailRecipientStyle> ResolveRecipientAsync(string? email, CancellationToken ct = default) =>
        ResolveRecipientAsync(email, MailTemplateLanguages.English, ct);

    public async Task<MailRecipientStyle> ResolveRecipientAsync(string? email, string defaultLanguage, CancellationToken ct = default)
    {
        var fallback = MailTemplateLanguages.Normalize(defaultLanguage);
        if (string.IsNullOrWhiteSpace(email)) return MailRecipientStyle.Default with { Language = fallback };
        var (language, appearance) = await repository.FindRecipientPreferencesAsync(email, ct).ConfigureAwait(false);
        return MailRecipientStyle.FromPreferences(language, appearance, fallback);
    }

    public Task<OmniMailRenderResult> RenderAsync(
        MailTemplateKind kind, MailTemplateData data, MailRecipientStyle style, CancellationToken ct = default) =>
        RenderAsync(kind, data, style, null, ct);

    public async Task<OmniMailRenderResult> RenderAsync(
        MailTemplateKind kind, MailTemplateData data, MailRecipientStyle style, int? organizationId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(style);
        var language = MailTemplateLanguages.Normalize(style.Language);
        var own = organizationId is { } organization && MailTemplateScopes.IsOrganizationKind(kind)
            ? await repository.FindForOrganizationAsync(organization, kind, language, ct).ConfigureAwait(false)
            : null;
        var stored = own is null ? await repository.FindAsync(kind, language, ct).ConfigureAwait(false) : null;
        var (subject, body) = own is not null ? (own.Subject, own.Body)
            : stored is not null ? (stored.Subject, stored.Body)
            : MailTemplateDefaults.For(kind, language);
        var result = Render(subject, body, data, style with { Language = language });
        if (result.MissingVariables.Count > 0)
            logger.LogWarning("[MailTemplates] {Kind} ({Language}) uses variables without a value: {Variables}",
                kind, language, string.Join(", ", result.MissingVariables));
        return result;
    }

    /// <summary>
    /// Renders a subject and a body of the kind, stored or not (the test of a draft uses it too): the values as
    /// text or link variables, the blocks as block variables. A name in both throws <see cref="ArgumentException"/>.
    /// </summary>
    internal static OmniMailRenderResult Render(string subject, string body, MailTemplateData data, MailRecipientStyle style) =>
        OmniMailRenderer.Render(body, data.Values, new OmniMailRenderOptions
        {
            Subject = subject,
            Theme = style.Theme,
            Language = style.Language,
            Blocks = data.Blocks,
            Width = 640
        });
}
