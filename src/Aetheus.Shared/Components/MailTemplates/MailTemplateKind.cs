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
    DailyRecap = 3
}
