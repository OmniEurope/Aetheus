// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// Where the daily recap is configured: plain rows of the platform settings table, all under one
/// prefix that the generic settings list hides (the recap has its own tab and endpoint).
/// </summary>
public static class DailyRecapSettingKeys
{
    public const string Prefix = "DailyRecap:";

    public const string Enabled = Prefix + "Enabled";
    public const string SendTime = Prefix + "SendTime";
    public const string TimeZone = Prefix + "TimeZone";
    public const string Language = Prefix + "Language";
    public const string Sections = Prefix + "Sections";
    public const string Recipients = Prefix + "Recipients";

    /// <summary>The local date (yyyy-MM-dd) the scheduler last claimed; a day is sent once, at most.</summary>
    public const string LastSentDate = Prefix + "LastSentDate";

    /// <summary>One line on what that scheduled send became.</summary>
    public const string LastResult = Prefix + "LastResult";
}
