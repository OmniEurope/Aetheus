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
            (MailTemplateKind.UserWelcome, MailTemplateLanguages.French) => ("Aetheus : choisissez votre mot de passe", WelcomeFr),
            (MailTemplateKind.UserWelcome, _) => ("Aetheus: choose your password", WelcomeEn),
            (MailTemplateKind.PasswordReset, MailTemplateLanguages.French) => ("Aetheus : choisir un nouveau mot de passe", ResetFr),
            (MailTemplateKind.PasswordReset, _) => ("Aetheus: choose a new password", ResetEn),
            (MailTemplateKind.SiteAdminGranted, MailTemplateLanguages.French) => ("Aetheus : vous êtes désormais administrateur du site", SiteAdminFr),
            (MailTemplateKind.SiteAdminGranted, _) => ("Aetheus: you are now a site administrator", SiteAdminEn),
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
        <p>{{ApprovalActions}}</p>
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
        <p>{{ApprovalActions}}</p>
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

    // R-14: a link to choose the password, never a password (owner decision of 2026-10-28). The welcome
    // template also carries the new link an administrator sends from the user's page, so it reads for both.
    private const string WelcomeEn = """
        <h2>Your Aetheus account</h2>
        <p>An administrator invites you to choose the password of your account <strong>{{UserName}}</strong>. You sign in with this address: <strong>{{SignInEmail}}</strong>.</p>
        <p>{{#button SetPasswordUrl}}Choose my password</p>
        <p>This link works once, for {{ValidDays}} days. Once it has expired, ask an administrator for a new one, or use "Forgot password?" on the sign-in page.</p>
        <hr>
        <p class="omni-font-size-small">Sent by Aetheus at an administrator's request. If you did not expect it, ignore this mail.<br><a href="{{SignInUrl}}">Sign in</a></p>
        """;

    private const string WelcomeFr = """
        <h2>Votre compte Aetheus</h2>
        <p>Un administrateur vous invite à choisir le mot de passe de votre compte <strong>{{UserName}}</strong>. Vous vous connectez avec cette adresse : <strong>{{SignInEmail}}</strong>.</p>
        <p>{{#button SetPasswordUrl}}Choisir mon mot de passe</p>
        <p>Ce lien ne sert qu'une fois, pendant {{ValidDays}} jours. Une fois expiré, demandez-en un nouveau à un administrateur, ou utilisez « Mot de passe oublié ? » sur la page de connexion.</p>
        <hr>
        <p class="omni-font-size-small">Envoyé par Aetheus à la demande d'un administrateur. Si vous ne l'attendiez pas, ignorez ce courriel.<br><a href="{{SignInUrl}}">Se connecter</a></p>
        """;

    private const string ResetEn = """
        <h2>Choose a new password</h2>
        <p>Someone asked to choose a new password for the Aetheus account <strong>{{UserName}}</strong> ({{SignInEmail}}).</p>
        <p>{{#button SetPasswordUrl}}Choose a new password</p>
        <p>This link works once, for {{ValidMinutes}} minutes. Your current password keeps working until you choose a new one.</p>
        <hr>
        <p class="omni-font-size-small">If you did not ask for it, ignore this mail: nothing changes.<br><a href="{{SignInUrl}}">Sign in</a></p>
        """;

    private const string ResetFr = """
        <h2>Choisir un nouveau mot de passe</h2>
        <p>Quelqu'un a demandé à choisir un nouveau mot de passe pour le compte Aetheus <strong>{{UserName}}</strong> ({{SignInEmail}}).</p>
        <p>{{#button SetPasswordUrl}}Choisir un nouveau mot de passe</p>
        <p>Ce lien ne sert qu'une fois, pendant {{ValidMinutes}} minutes. Votre mot de passe actuel reste valable tant que vous n'en choisissez pas un nouveau.</p>
        <hr>
        <p class="omni-font-size-small">Si vous n'avez rien demandé, ignorez ce courriel : rien ne change.<br><a href="{{SignInUrl}}">Se connecter</a></p>
        """;

    // Sent once per grant of the site Admin role, never when the role was already held: a person learns at
    // once that their account can now change everything, and can raise the alarm if they did not expect it.
    private const string SiteAdminEn = """
        <h2>You are now a site administrator</h2>
        <p>Your Aetheus account <strong>{{UserName}}</strong> ({{SignInEmail}}) now holds the site Admin role: it has full access to every organization and to the platform settings.</p>
        <p>{{#button SignInUrl}}Sign in</p>
        <hr>
        <p class="omni-font-size-small">If you did not expect this change, tell another administrator of the platform at once.</p>
        """;

    private const string SiteAdminFr = """
        <h2>Vous êtes désormais administrateur du site</h2>
        <p>Votre compte Aetheus <strong>{{UserName}}</strong> ({{SignInEmail}}) détient désormais le rôle Admin du site : il a un accès complet à toutes les organisations et aux réglages de la plateforme.</p>
        <p>{{#button SignInUrl}}Se connecter</p>
        <hr>
        <p class="omni-font-size-small">Si vous n'attendiez pas ce changement, prévenez sans attendre un autre administrateur de la plateforme.</p>
        """;
}
