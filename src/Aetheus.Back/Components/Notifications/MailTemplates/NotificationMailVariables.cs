// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>The names of the variables the notification templates insert, written <c>{{Name}}</c>.</summary>
public static class NotificationMailVariables
{
    /// <summary>The name of the tested channel.</summary>
    public const string ChannelName = "ChannelName";

    /// <summary>The address of the notification inbox; empty when no web address is configured.</summary>
    public const string InboxUrl = "InboxUrl";

    /// <summary>What happened, in words (« Pipeline failed »).</summary>
    public const string Event = "Event";

    /// <summary>The event and its named subject on one line, as the subject used to read.</summary>
    public const string Summary = "Summary";

    /// <summary>The sentence explaining the event.</summary>
    public const string Intro = "Intro";

    /// <summary>The named subject of the event (pipeline, server, application); empty when it has none.</summary>
    public const string Context = "Context";

    /// <summary>The project of the event; empty when it has none.</summary>
    public const string Project = "Project";

    /// <summary>The page that shows the event (its run, server, release...); empty when no web address is configured.</summary>
    public const string Link = "Link";

    /// <summary>The words of the action button; empty, so no button, when there is no link.</summary>
    public const string LinkLabel = "LinkLabel";

    /// <summary>The time the mail was written, in the platform wall clock with its zone named; it differs from one mail to the next.</summary>
    public const string SentAt = "SentAt";

    /// <summary>Block: the named subject on its own line, a link to its server when it is one.</summary>
    public const string ContextLink = "ContextLink";

    /// <summary>Block: the banner with the event in the colours of its status.</summary>
    public const string StatusBanner = "StatusBanner";

    /// <summary>Block: the table of the event's details, statuses as coloured badges.</summary>
    public const string Details = "Details";

    /// <summary>Block: the root run of a child run's lineage, or why the mail carries no link; empty otherwise.</summary>
    public const string LinkNote = "LinkNote";
}
