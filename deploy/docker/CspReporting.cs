// SPDX-License-Identifier: EUPL-1.2

/// <summary>Names the API CSP collector for the shell and Monaco frame through report-to and
/// Reporting-Endpoints, avoiding the deprecated report-uri directive.</summary>
internal static class CspReporting
{
    /// <summary>The Reporting-Endpoints name the policy's report-to directive refers to.</summary>
    internal const string EndpointName = "csp-endpoint";

    /// <summary>The trailing report-to directive, or nothing when no collector is known.</summary>
    internal static string Directive(string? reportUri) =>
        reportUri is null ? string.Empty : $"; report-to {EndpointName}";
}
