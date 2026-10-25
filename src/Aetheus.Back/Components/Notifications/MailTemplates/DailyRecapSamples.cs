// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Services.Email;
using static Aetheus.Back.Components.Notifications.MailTemplates.DailyRecapMailVariables;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>
/// The sample values of the daily recap template, for the editor's preview and the test mail: two tiles
/// and one section drawn with the fragments the recap itself uses (<see cref="EmailHtmlLayout"/>). The
/// recap's own module sits above this one, so its composer is not called from here.
/// </summary>
internal static class DailyRecapSamples
{
    private const string SuccessFill = "#e3f4e8";
    private const string SuccessInk = "#17643a";
    private const string DangerFill = "#fde8e6";
    private const string DangerInk = "#a3231a";

    public static MailTemplateData For(bool french, string origin)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Date] = french ? "jeudi 8 octobre 2026" : "Thursday, 8 October 2026",
            [Period] = string.Empty,
            [SettingsUrl] = origin + "/admin/settings?tab=daily-recap"
        };
        var tiles = EmailHtmlLayout.Tiles(
        [
            ("3", french ? "exécutions de pipeline réussies" : "successful pipeline runs", SuccessFill, SuccessInk),
            ("1", french ? "exécutions de pipeline en échec" : "failed pipeline runs", DangerFill, DangerInk)
        ]);
        var sections = new StringBuilder();
        EmailHtmlLayout.Section(sections, "Pipelines", french ? "4 exécutions : 3 réussies, 1 en échec." : "4 runs: 3 succeeded, 1 failed.")
            .Append("<table>");
        EmailHtmlLayout.ItemRow(sections);
        EmailHtmlLayout.Badge(sections, DangerFill, DangerInk, french ? "Échec" : "Failed")
            .Append(" <a href=\"").Append(origin).Append("/pipelines/runs/4821\"><strong>release-candidate</strong></a><br>")
            .Append("<span style=\"font-size:13px;color:").Append(EmailHtmlLayout.Muted).Append("\">Shop · develop · ")
            .Append(french ? "exécution n° 4821 à 16:42" : "run #4821 at 4:42 PM").Append("</span></td></tr></table>");
        return new MailTemplateData(values, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Tiles] = tiles,
            [Sections] = sections.ToString()
        });
    }
}
