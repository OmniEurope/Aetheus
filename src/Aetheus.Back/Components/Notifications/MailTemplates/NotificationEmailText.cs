// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Resources;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>
/// The words of a notification mail in the recipient's language (recette R1-74), read from
/// NotificationEmailStrings.resx (English) and NotificationEmailStrings.fr.resx (French); never the
/// sender's culture.
/// </summary>
internal static class NotificationEmailText
{
    private static readonly ResourceManager Resources =
        new("Aetheus.Back.Components.Notifications.MailTemplates.NotificationEmailStrings", typeof(NotificationEmailText).Assembly);

    public static string Get(string key, string language) => Resources.GetString(key, Culture(language))
        ?? throw new InvalidOperationException($"Missing notification email string '{key}'.");

    public static string Label(string key, string language) => Resources.GetString(key, Culture(language)) ?? key;

    /// <summary>The translation of a key, or null when the resources hold none (a value the mail has no word for).</summary>
    public static string? Find(string key, string language) => Resources.GetString(key, Culture(language));

    private static CultureInfo Culture(string language) => CultureInfo.GetCultureInfo(MailTemplateLanguages.Normalize(language));
}
