// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Certbot;

/// <summary>Recette R-69: the bounds of the certificate expiry alert lead times, shared by the settings
/// page and the backend so both refuse the same values.</summary>
public static class CertificateAlertLeadTimes
{
    public const int MinDays = 1;
    public const int MaxDays = 365;

    /// <summary>How many lead times can be listed; far more than anyone needs, but bounded.</summary>
    public const int MaxCount = 20;

    /// <summary>The lead times in force until an administrator saves a list of their own.</summary>
    public static IReadOnlyList<int> Default { get; } = [7, 3, 1];

    /// <summary>The "expires soon" horizon of the Certbot page when no lead time is configured (no alert at all).</summary>
    public const int FallbackWarningDays = 30;
}

/// <summary>Recette R-69: how many days before a certificate expires the platform raises an alert, one per lead time.</summary>
public sealed record CertificateAlertSettingsDto
{
    /// <summary>The lead times in days, longest first; empty means no certificate expiry alert at all.</summary>
    [MaxLength(CertificateAlertLeadTimes.MaxCount)]
    public List<int> LeadDays { get; init; } = [.. CertificateAlertLeadTimes.Default];
}

public sealed record UpdateCertificateAlertSettingsRequest
{
    /// <summary>Each value from <see cref="CertificateAlertLeadTimes.MinDays"/> to
    /// <see cref="CertificateAlertLeadTimes.MaxDays"/>, no duplicate; empty switches the alerts off.</summary>
    [Required]
    [MaxLength(CertificateAlertLeadTimes.MaxCount)]
    public List<int> LeadDays { get; init; } = [];
}

/// <summary>Recette R-69: the horizon the Certbot page marks a certificate "expires soon" from.</summary>
public sealed record CertificateExpiryWarningDto
{
    public int Days { get; init; } = CertificateAlertLeadTimes.FallbackWarningDays;
}
