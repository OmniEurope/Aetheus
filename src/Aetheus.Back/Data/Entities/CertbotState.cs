// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>Reports server-level Certbot state while Certbot is installed, including the latest
/// renewal rehearsal result so failures are visible before certificate expiry.</summary>
public class CertbotState
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public DateTime? RenewalCheckedAt { get; set; }
    public bool? RenewalCheckSucceeded { get; set; }

    /// <summary>Version of the aetheus-certbot-manage helper the agent last reported (0 = absent or unversioned).</summary>
    public int HelperVersion { get; set; }

    /// <summary>Recette R3-56: the lineages the last rehearsal failed and certbot's reason, as JSON
    /// (<c>CertbotRenewalFailureDto</c> list); null when none were reported.</summary>
    public string? RenewalCheckFailuresJson { get; set; }

    /// <summary>Recette R1-7: the last meaningful lines certbot printed during the last failed rehearsal, as a JSON
    /// array of strings; null when none were reported.</summary>
    public string? RenewalCheckOutputTailJson { get; set; }

    /// <summary>Recette R-2: where a proxying Apache RewriteRule still lets the ACME challenge reach the
    /// application (<c>file:line</c>), as a JSON array of strings; null when none were reported.</summary>
    public string? AcmeProxyRulesJson { get; set; }

    // Navigation
    public Server Server { get; set; } = null!;
}
