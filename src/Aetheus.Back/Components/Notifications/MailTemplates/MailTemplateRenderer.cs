// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

public sealed class MailTemplateRenderer(IMailTemplateRepository repository, ILogger<MailTemplateRenderer> logger) : IMailTemplateRenderer
{
    public async Task<MailRecipientStyle> ResolveRecipientAsync(string? email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return MailRecipientStyle.Default;
        var (language, appearance) = await repository.FindRecipientPreferencesAsync(email, ct).ConfigureAwait(false);
        return MailRecipientStyle.FromPreferences(language, appearance);
    }

    public async Task<OmniMailRenderResult> RenderAsync(
        MailTemplateKind kind, MailTemplateData data, MailRecipientStyle style, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(style);
        var language = MailTemplateLanguages.Normalize(style.Language);
        var stored = await repository.FindAsync(kind, language, ct).ConfigureAwait(false);
        var (subject, body) = stored is null ? MailTemplateDefaults.For(kind, language) : (stored.Subject, stored.Body);
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
