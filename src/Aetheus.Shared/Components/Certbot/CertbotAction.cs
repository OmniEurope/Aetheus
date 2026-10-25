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
    RemoveDomain
}
