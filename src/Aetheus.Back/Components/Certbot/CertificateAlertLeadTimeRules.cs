// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Certbot;

/// <summary>
/// Recette R-69: what a list of certificate alert lead times means. The list is one row of the platform
/// settings table (<see cref="LeadDaysKey"/>), kept as the lead times in days separated by commas, longest
/// first. A missing row is the default list; a present but empty row means "no alert".
/// </summary>
internal static class CertificateAlertLeadTimeRules
{
    /// <summary>All the certificate alert rows share this prefix; the generic settings list hides it.</summary>
    internal const string KeyPrefix = "CertificateAlerts:";

    internal const string LeadDaysKey = KeyPrefix + "LeadDays";

    /// <summary>The saved list, or the default one while none was ever saved. A value that is not a number is dropped.</summary>
    internal static List<int> Parse(string? stored)
    {
        if (stored is null) return [.. CertificateAlertLeadTimes.Default];
        return [.. stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var days) ? days : 0)
            .Where(IsInRange)
            .Distinct()
            .OrderByDescending(days => days)];
    }

    internal static string Serialize(IEnumerable<int> leadDays) =>
        string.Join(',', leadDays.Select(days => days.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The list as the request states it, sorted longest first; refuses a value out of range or listed twice.</summary>
    internal static List<int> Validate(IReadOnlyCollection<int>? requested)
    {
        if (requested is null) throw new BadRequestException("The list of lead times is required.");
        if (requested.Count > CertificateAlertLeadTimes.MaxCount)
            throw new BadRequestException($"At most {CertificateAlertLeadTimes.MaxCount} lead times can be listed.");
        if (requested.Any(days => !IsInRange(days)))
            throw new BadRequestException(
                $"A lead time is a number of days from {CertificateAlertLeadTimes.MinDays} to {CertificateAlertLeadTimes.MaxDays}.");
        if (requested.Distinct().Count() != requested.Count)
            throw new BadRequestException("A lead time can be listed once only.");
        return [.. requested.OrderByDescending(days => days)];
    }

    /// <summary>
    /// The horizon the Certbot page marks "expires soon" from: the longest lead time, since that is when the
    /// first alert is raised; the fixed fallback when no alert is configured.
    /// </summary>
    internal static int WarningDays(IReadOnlyCollection<int> leadDays) =>
        leadDays.Count == 0 ? CertificateAlertLeadTimes.FallbackWarningDays : leadDays.Max();

    /// <summary>The lead times a certificate expiring at <paramref name="expiryUtc"/> has reached at <paramref name="nowUtc"/>.</summary>
    internal static List<int> Crossed(IEnumerable<int> leadDays, DateTime expiryUtc, DateTime nowUtc) =>
        [.. leadDays.Where(days => expiryUtc <= nowUtc.AddDays(days))];

    private static bool IsInRange(int days) =>
        days is >= CertificateAlertLeadTimes.MinDays and <= CertificateAlertLeadTimes.MaxDays;
}
