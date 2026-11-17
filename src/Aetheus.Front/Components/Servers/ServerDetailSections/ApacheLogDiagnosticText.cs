// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Recette R-19: the sentence the Apache log tab shows when the agent answered with an
/// <see cref="ApacheLogDiagnostic"/> line instead of log lines; it names the path, and for a log the agent may
/// not read, what grants that read. Null when the output holds log lines.</summary>
public static class ApacheLogDiagnosticText
{
    public static string? Describe(IStringLocalizer localizer, string? output)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (!ApacheLogDiagnostic.TryParse(output, out var outcome, out var paths)) return null;

        return outcome switch
        {
            ApacheLogReadOutcome.NotFound => localizer["ApacheLogNotFound", string.Join(", ", paths)].Value,
            ApacheLogReadOutcome.AccessDenied => localizer["ApacheLogAccessDenied", paths[0]].Value,
            _ => localizer["ApacheLogReadError", paths[0]].Value
        };
    }
}
