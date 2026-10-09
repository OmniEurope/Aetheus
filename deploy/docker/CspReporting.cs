// SPDX-License-Identifier: EUPL-1.2

/// <summary>
/// Recette R2-108: how both front documents (the shell and the Monaco frame) name the API's CSP
/// collector. report-to and the Reporting-Endpoints header, never report-uri: a policy carrying
/// report-uri is reported by the DAST scan as "CSP: Notices" (ZAP 10055-3) for that directive alone.
/// </summary>
internal static class CspReporting
{
    /// <summary>The Reporting-Endpoints name the policy's report-to directive refers to.</summary>
    internal const string EndpointName = "csp-endpoint";

    /// <summary>The trailing report-to directive, or nothing when no collector is known.</summary>
    internal static string Directive(string? reportUri) =>
        reportUri is null ? string.Empty : $"; report-to {EndpointName}";
}
