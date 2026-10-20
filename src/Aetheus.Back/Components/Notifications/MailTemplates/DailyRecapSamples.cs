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
            [Period] = french
                ? "Journée de 00:00 à 24:00 (Europe/Paris). Généré le 09/10/2026 07:00."
                : "Day from 00:00 to 24:00 (Europe/Paris). Generated on 10/9/2026 7:00 AM.",
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
        EmailHtmlLayout.Badge(sections, DangerFill, DangerInk, french ? "Échec" : "Failed").Append(" <strong>release-candidate</strong><br>")
            .Append("<span style=\"font-size:13px;color:").Append(EmailHtmlLayout.Muted).Append("\">Shop · develop</span><br>")
            .Append("<a href=\"").Append(origin).Append("/pipelines/runs/4821\" style=\"font-size:13px;font-weight:600\">")
            .Append(french ? "Voir l’exécution et les journaux" : "View execution and logs").Append("</a></td></tr></table>");
        return new MailTemplateData(values, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Tiles] = tiles,
            [Sections] = sections.ToString()
        });
    }
}
