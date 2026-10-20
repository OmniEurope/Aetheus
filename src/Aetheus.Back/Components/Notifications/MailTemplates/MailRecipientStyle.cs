// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using OmniEurope.Blazor.Components;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>
/// The language and the colours a mail is written in for its recipient (recette R1-74): the recipient's
/// own language and appearance preferences (R1-66), read on the server. A recipient with no account or no
/// preference gets the platform default, English and the shipped OE theme: Aetheus has no organization
/// or site default language or theme to fall back on.
/// </summary>
public sealed record MailRecipientStyle(string Language, OmniMailTheme Theme)
{
    /// <summary>The preference key of the language, as the front writes it (<c>"fr-FR"</c> or <c>"en"</c>).</summary>
    public const string LanguagePreferenceKey = "aetheus.language";

    /// <summary>The preference key of the appearance, as the front writes it (theme, palette and font by name).</summary>
    public const string AppearancePreferenceKey = "aetheus.appearance";

    public static MailRecipientStyle Default { get; } = new(MailTemplateLanguages.English, OmniMailTheme.Default);

    /// <summary>The style of the stored preference values (JSON, as the browser wrote them); a value that does not read keeps the default.</summary>
    public static MailRecipientStyle FromPreferences(string? languageJson, string? appearanceJson) =>
        new(ReadLanguage(languageJson), ReadTheme(appearanceJson));

    private static string ReadLanguage(string? json) =>
        ReadJson(json) is { ValueKind: JsonValueKind.String } value
            ? MailTemplateLanguages.Normalize(value.GetString())
            : MailTemplateLanguages.English;

    /// <summary>The theme and palette of the appearance, painted light: a mail reads light in every client.</summary>
    private static OmniMailTheme ReadTheme(string? json)
    {
        if (ReadJson(json) is not { ValueKind: JsonValueKind.Object } appearance)
            return OmniMailTheme.Default;
        var presetName = Text(appearance, "preset");
        var paletteName = Text(appearance, "palette");
        var preset = OmniThemePresets.All.FirstOrDefault(item => item.Name == presetName) ?? OmniThemePresets.All[0];
        var palette = OmniThemePalettes.All.FirstOrDefault(item => item.Name == paletteName);
        return OmniMailTheme.From(palette is { } chosen ? preset.With(chosen) : preset);
    }

    private static string? Text(JsonElement appearance, string name)
    {
        foreach (var property in appearance.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }
        return null;
    }

    private static JsonElement? ReadJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Mandatory: a preference stored in another shape reads as the default, never as a failed mail.
            return null;
        }
    }
}
