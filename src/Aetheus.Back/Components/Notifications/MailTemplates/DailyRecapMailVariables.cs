// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>The names of the variables the daily recap template inserts, written <c>{{Name}}</c>.</summary>
public static class DailyRecapMailVariables
{
    /// <summary>The day the recap covers, written in the recap's language.</summary>
    public const string Date = "Date";

    /// <summary>The period and the time zone of the recap, and when it was generated.</summary>
    public const string Period = "Period";

    /// <summary>The recap settings page; empty when no web address is configured.</summary>
    public const string SettingsUrl = "SettingsUrl";

    /// <summary>Block: the key figures, one coloured tile each.</summary>
    public const string Tiles = "Tiles";

    /// <summary>Block: the sections (pipelines, servers, releases...), their items and status badges.</summary>
    public const string Sections = "Sections";
}
