// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Certbot;

// --- Aggregate heartbeat data ---

public sealed record CertbotDataDto
{
    public bool IsInstalled { get; init; }
    [StringLength(100)]
    public string Version { get; init; } = string.Empty;
    [MaxLength(2048)]
    public List<CertbotCertificateDto> Certificates { get; init; } = [];
    // Outcome of the last renewal rehearsal (`certbot renew --dry-run`, weekly timer or on demand).
    // Null when none was recorded yet.
    public DateTime? RenewalCheckedAt { get; init; }
    public bool? RenewalCheckSucceeded { get; init; }

    /// <summary>Version of the root-owned <c>aetheus-certbot-manage</c> helper installed on the server (0 when
    /// absent or unversioned, which is older than <see cref="CertbotHelperVersion.Current"/>).</summary>
    public int HelperVersion { get; init; }

    /// <summary>Recette R3-56: the lineages the last rehearsal failed, each with certbot's reason. Empty
    /// when it passed, or when certbot printed no per-lineage reason.</summary>
    [MaxLength(CertbotRenewalFailureDto.MaxFailures)]
    public List<CertbotRenewalFailureDto> RenewalCheckFailures { get; init; } = [];

    public const int MaxOutputTailLines = 20;
    public const int MaxOutputTailLineLength = 300;

    /// <summary>Recette R1-7: the last meaningful lines certbot printed during the last failed rehearsal (the
    /// first one says how long it ran and its exit code), so the cause stays visible when no per-lineage
    /// reason was recognised. Empty when it passed, or when the helper predates the capture.</summary>
    [MaxLength(MaxOutputTailLines)]
    [Aetheus.Shared.Components.Shared.MaxItemStringLength(MaxOutputTailLineLength)]
    public List<string> RenewalCheckOutputTail { get; init; } = [];

    public const int MaxAcmeProxyRules = 50;
    public const int MaxAcmeProxyRuleLength = 400;

    /// <summary>Recette R-2: where an Apache <c>RewriteRule ... [P]</c> still lets the ACME challenge reach the
    /// application (<c>&lt;file&gt;:&lt;line&gt;</c>), so the certificates of that virtual host fail the dry run. The
    /// helper protects the files it may edit when it normalizes; what it lists here needs a manual change.
    /// Empty when there is none, or when the helper predates the check.</summary>
    [MaxLength(MaxAcmeProxyRules)]
    [Aetheus.Shared.Components.Shared.MaxItemStringLength(MaxAcmeProxyRuleLength)]
    public List<string> AcmeProxyRules { get; init; } = [];
}

/// <summary>Recette R3-56: one certificate that failed <c>certbot renew --dry-run</c>, and why.</summary>
public sealed record CertbotRenewalFailureDto
{
    public const int MaxFailures = 256;
    public const int MaxNameLength = 255;
    public const int MaxReasonLength = 500;

    [StringLength(MaxNameLength)]
    public string Name { get; init; } = string.Empty;

    [StringLength(MaxReasonLength)]
    public string Reason { get; init; } = string.Empty;
}

// --- Certificate info ---

public sealed record CertbotCertificateDto
{
    [StringLength(255)]
    public string Name { get; init; } = string.Empty;
    [MaxLength(100)]
    [Aetheus.Shared.Components.Shared.MaxItemStringLength(253)]
    public List<string> Domains { get; init; } = [];
    public DateTime ExpiryDate { get; init; }
    [StringLength(4096)]
    public string CertPath { get; init; } = string.Empty;
    [StringLength(4096)]
    public string KeyPath { get; init; } = string.Empty;
    // Renewal method read from /etc/letsencrypt/renewal/<name>.conf.
    [StringLength(100)]
    public string Authenticator { get; init; } = string.Empty;
    [StringLength(4096)]
    public string WebrootPath { get; init; } = string.Empty;
    public Aetheus.Shared.Components.Certbot.CertbotRenewalConvention RenewalConvention { get; init; }
    // "Expiring soon" is intentionally NOT a computed DTO property: it depends on the current
    // time, not on the payload, so it is evaluated by the consumer against ExpiryDate (the
    // front uses browser-local DateTime.Now). Keeping it here would bake server UtcNow into
    // the serialized contract.
}

// --- Action request ---

public sealed record CertbotActionRequest
{
    [Required]
    public Aetheus.Shared.Components.Certbot.CertbotAction Action { get; init; }

    [StringLength(200)]
    public string? CertificateName { get; init; }

    /// <summary>Recette R-12: the name <see cref="Aetheus.Shared.Components.Certbot.CertbotAction.RemoveDomain"/>
    /// takes out of the certificate; unused by the other actions.</summary>
    [StringLength(253)]
    public string? DomainName { get; init; }
}

// --- Create request ---

public sealed record CertbotCreateRequest
{
    [Required]
    [StringLength(1000, MinimumLength = 1)]
    public string Domains { get; init; } = string.Empty;

    [EmailAddress]
    [StringLength(200)]
    public string? Email { get; init; }

    public bool Standalone { get; init; }

    [StringLength(500)]
    public string? WebrootPath { get; init; }
}

/// <summary>The <c>aetheus-certbot-manage</c> helper version this release ships; a server reporting less
/// (or none) is shown as "to update". Bumped together with the <c>aetheus-certbot-helper-version</c> marker.</summary>
public static class CertbotHelperVersion
{
    public const int Current = 9;
}

/// <summary>Task variables of the Certbot operations shared by the backend that queues them and the agent.</summary>
public static class CertbotTaskVariables
{
    /// <summary>Recette R-12: the name a <c>CertbotRemoveDomain</c> operation takes out of its certificate
    /// (the target being the certificate name).</summary>
    public const string RemoveDomain = "AETHEUS_CERTBOT_REMOVE_DOMAIN";
}
