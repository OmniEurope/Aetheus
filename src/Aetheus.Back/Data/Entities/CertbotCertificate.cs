// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public class CertbotCertificate
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Domains { get; set; } = "[]"; // JSON array
    public DateTime ExpiryDate { get; set; }
    public string CertPath { get; set; } = string.Empty;
    public string KeyPath { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }

    // Records the renewal method read by the agent from the lineage renewal file.
    public string Authenticator { get; set; } = string.Empty;
    public string WebrootPath { get; set; } = string.Empty;
    public CertbotRenewalConvention RenewalConvention { get; set; }

    // Navigation
    public Server Server { get; set; } = null!;
}
