// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Certbot;

public enum CertbotAction
{
    Renew,
    RenewAll,
    Create,
    Delete,
    RevokeAndDelete,
    Normalize,
    RenewalCheck,
    /// <summary>Recette R-12: reissues a certificate without one of its names (the certificate is kept).</summary>
    RemoveDomain,
    /// <summary>Recette R-19: rehearses only the certificates the last test reported as failed (the server reads them from its recorded state).</summary>
    RenewalCheckFailed
}
