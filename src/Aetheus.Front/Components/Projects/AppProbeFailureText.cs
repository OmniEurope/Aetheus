// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Projects;

/// <summary>
/// R-29: the sentence the user reads for a probe failure, chosen from the stored reason code (never an exception
/// message). A row written before the codes existed keeps its plain text and is shown as it is.
/// </summary>
internal static class AppProbeFailureText
{
    internal static string Describe(IStringLocalizer<AppStrings> L, string? error)
    {
        if (string.IsNullOrEmpty(error))
            return L["NotAvailable"].Value;
        if (AppProbeFailure.TryParse(error) is not { } failure)
            return error;

        var host = failure.Host ?? L["NotAvailable"].Value;
        return failure.Code switch
        {
            AppProbeFailure.ForbiddenTarget => Format(L["AppProbeFailForbiddenTarget"], host),
            AppProbeFailure.DnsFailure => Format(L["AppProbeFailDns"], host),
            AppProbeFailure.ConnectionRefused => Format(L["AppProbeFailRefused"], host),
            AppProbeFailure.TimedOut => Format(L["AppProbeFailTimeout"], host),
            AppProbeFailure.TlsFailure => Format(L["AppProbeFailTls"], host),
            AppProbeFailure.UnexpectedStatus => Format(L["AppProbeFailStatus"],
                failure.Status?.ToString(CultureInfo.CurrentCulture) ?? "-",
                failure.Expected?.ToString(CultureInfo.CurrentCulture) ?? "-"),
            _ => Format(L["AppProbeFailUnreachable"], host)
        };
    }

    private static string Format(string pattern, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, pattern, values);
}
