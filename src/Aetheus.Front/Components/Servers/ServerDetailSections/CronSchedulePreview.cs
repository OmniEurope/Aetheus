// SPDX-License-Identifier: EUPL-1.2
using Cronos;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Previews a cron schedule for the cron job dialog and the cron section.</summary>
internal static class CronSchedulePreview
{
    internal static (bool IsValid, string? NextRun, string? Error) ValidateCronExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return (false, null, null);
        try
        {
            // Standard 5-field only: /etc/cron.d entries are 5-field, and a 6-field (seconds) schedule is
            // rejected at save (CronValidation.IsValidScheduleSyntax), so the preview must mirror that.
            var cron = CronExpression.Parse(expression, CronFormat.Standard);
            var next = cron.GetNextOccurrence(DateTime.UtcNow);
            return (true, next?.ToLocalTime().ToString("g"), null);
        }
        catch (CronFormatException ex)
        {
            return (false, null, ex.Message);
        }
    }
}
