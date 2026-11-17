// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Analysis;

/// <summary>
/// How long an accepted risk lasts before its finding is reopened (PLAN-004 decision 4, recette R-24).
/// Only the three lengths of the decision are valid; the server turns the length into the expiry date.
/// </summary>
public static class AcceptedRiskDuration
{
    /// <summary>The scanner key of the findings the CVE watch publishes: the only ones whose acceptance
    /// must carry a length.</summary>
    public const string CveWatchScannerKey = "cve-watch";

    /// <summary>The lengths a risk can be accepted for, in days.</summary>
    public static IReadOnlyList<int> AllowedDays { get; } = [30, 90, 180];

    /// <summary>The length offered first.</summary>
    public const int DefaultDays = 90;

    public static bool IsAllowed(int days) => AllowedDays.Contains(days);
}
