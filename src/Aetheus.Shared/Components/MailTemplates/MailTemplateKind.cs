// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.MailTemplates;

/// <summary>
/// The mails Aetheus sends from an editable template (recette R1-75): each kind has its own variables,
/// and one template per language. Since OmniEurope.Blazor 1.8.2 draws an action button and block
/// variables, the event notifications and the daily recap are kinds too: their status banner, detail
/// table, recap tiles and sections are HTML fragments Aetheus writes and the template places.
/// </summary>
public enum MailTemplateKind
{
    /// <summary>The test message of an E-mail notification channel.</summary>
    NotificationChannelTest = 1,

    /// <summary>The mail of a subscribed event (a failed pipeline, a server offline...).</summary>
    NotificationEvent = 2,

    /// <summary>The daily recap sent to its recipients.</summary>
    DailyRecap = 3,

    /// <summary>The welcome mail of an account an administrator created, or a new link an administrator sent:
    /// a link to choose its password (R-14).</summary>
    UserWelcome = 4,

    /// <summary>The "forgotten password" mail: a one-hour link to choose a new password (R-14).</summary>
    PasswordReset = 5,

    /// <summary>The mail telling a person they newly hold the site Admin role: given through the user or role
    /// administration, or by the bootstrap of the configured administrator address.</summary>
    SiteAdminGranted = 6
}
