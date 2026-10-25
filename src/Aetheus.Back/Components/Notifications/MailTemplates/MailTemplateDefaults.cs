// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>
/// The templates a kind is sent with until an administrator changes them, in English and in French:
/// the words the mail carried before templates existed. They are code, not rows (decision of 2026-10-13),
/// so a later wording change reaches every platform that kept the default.
/// </summary>
/// <remarks>
/// A block variable stands alone in its paragraph, where its fragment replaces the paragraph. The event
/// banner is a block (<c>{{StatusBanner}}</c>), not OmniMailRenderer's <c>{{#banner tone}}</c>: that tone is
/// a word of the template, which no value can change, so one template could not colour a failure red and a
/// success green; one template per tone would split each language's notification into four copies to keep
/// in step. The block keeps one template per language, which the administrator still edits around it.
/// </remarks>
internal static class MailTemplateDefaults
{
    public static (string Subject, string Body) For(MailTemplateKind kind, string language) =>
        (kind, MailTemplateLanguages.Normalize(language)) switch
        {
            (MailTemplateKind.NotificationChannelTest, MailTemplateLanguages.French) =>
                ("[Aetheus] Test du canal de notification : « {{ChannelName}} »", ChannelTestFr),
            (MailTemplateKind.NotificationChannelTest, _) =>
                ("[Aetheus] Notification channel test: \"{{ChannelName}}\"", ChannelTestEn),
            (MailTemplateKind.NotificationEvent, MailTemplateLanguages.French) => ("[Aetheus] {{Summary}}", EventFr),
            (MailTemplateKind.NotificationEvent, _) => ("[Aetheus] {{Summary}}", EventEn),
            (MailTemplateKind.DailyRecap, MailTemplateLanguages.French) => ("Récapitulatif quotidien Aetheus - {{Date}}", RecapFr),
            (MailTemplateKind.DailyRecap, _) => ("Aetheus daily recap - {{Date}}", RecapEn),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown mail template kind.")
        };

    private const string ChannelTestEn = """
        <h2>Notification channel test</h2>
        <p>This is a test of the notification channel. Receiving this message confirms delivery to this address.</p>
        <p>Channel: <strong>{{ChannelName}}</strong></p>
        <hr>
        <p class="omni-font-size-small">Sent by Aetheus at the request of the person testing this notification channel.<br><a href="{{InboxUrl}}">Open notifications</a></p>
        """;

    private const string ChannelTestFr = """
        <h2>Test du canal de notification</h2>
        <p>Ceci est un test du canal de notification. Recevoir ce message confirme la remise à cette adresse.</p>
        <p>Canal : <strong>{{ChannelName}}</strong></p>
        <hr>
        <p class="omni-font-size-small">Envoyé par Aetheus à la demande de la personne qui teste ce canal de notification.<br><a href="{{InboxUrl}}">Ouvrir les notifications</a></p>
        """;

    private const string EventEn = """
        <p>{{StatusBanner}}</p>
        <p>{{Intro}}</p>
        <p>{{ContextLink}}</p>
        <h3>Event details</h3>
        <p>{{Details}}</p>
        <p>{{#button Link}}{{LinkLabel}}</p>
        <p>{{LinkNote}}</p>
        <hr>
        <p class="omni-font-size-small">Sent by Aetheus on {{SentAt}} according to notification subscriptions or platform notification rules.<br><a href="{{InboxUrl}}">Open notifications</a></p>
        """;

    private const string EventFr = """
        <p>{{StatusBanner}}</p>
        <p>{{Intro}}</p>
        <p>{{ContextLink}}</p>
        <h3>Détails de l'événement</h3>
        <p>{{Details}}</p>
        <p>{{#button Link}}{{LinkLabel}}</p>
        <p>{{LinkNote}}</p>
        <hr>
        <p class="omni-font-size-small">Envoyé par Aetheus le {{SentAt}} selon les abonnements aux notifications ou les règles de notification de la plateforme.<br><a href="{{InboxUrl}}">Ouvrir les notifications</a></p>
        """;

    private const string RecapEn = """
        <p class="omni-font-size-small"><strong>AETHEUS</strong> · Your daily overview</p>
        <h2>What happened on {{Date}}</h2>
        <p>{{Tiles}}</p>
        <p>{{Sections}}</p>
        <hr>
        <p class="omni-font-size-small">Sent by Aetheus<br><a href="{{SettingsUrl}}">Change the recap settings</a></p>
        """;

    private const string RecapFr = """
        <p class="omni-font-size-small"><strong>AETHEUS</strong> · Votre panorama quotidien</p>
        <h2>Ce qui s'est passé le {{Date}}</h2>
        <p>{{Tiles}}</p>
        <p>{{Sections}}</p>
        <hr>
        <p class="omni-font-size-small">Envoyé par Aetheus<br><a href="{{SettingsUrl}}">Régler le récap</a></p>
        """;
}
