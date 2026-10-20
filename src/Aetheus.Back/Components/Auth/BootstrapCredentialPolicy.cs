// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Auth;

/// <summary>Which bootstrap identity a login request satisfied, and how long its token may live.</summary>
internal sealed record BootstrapMatch(DateTimeOffset? ExpiresAt);

/// <summary>Separates the persistent bootstrap administrator, usable only before user seeding, from
/// the expiring per-run deployment identity. This lets deployment smoke tests authenticate without
/// modifying persisted user passwords.</summary>
internal static class BootstrapCredentialPolicy
{
    internal static BootstrapMatch? Match(
        LoginRequest request, IConfiguration config, TimeProvider timeProvider, bool hasDbUsers)
    {
        var deploymentExpiresAt = DeploymentBootstrapIdentity.GetActiveExpiration(config, timeProvider);
        if (hasDbUsers && deploymentExpiresAt is null) return null;

        if (deploymentExpiresAt is not null)
        {
            var deployUser = config["Auth:DeploymentBootstrapUser"];
            var deployPass = config["Auth:DeploymentBootstrapPassword"];
            if (!string.IsNullOrEmpty(deployUser) && !string.IsNullOrEmpty(deployPass)
                && DeploymentBootstrapIdentity.CredentialsMatch(request, deployUser, deployPass))
            {
                return new BootstrapMatch(deploymentExpiresAt);
            }
        }

        if (hasDbUsers) return null;

        var adminUser = config["Auth:AdminUser"] ?? "admin";
        var adminPass = config["Auth:AdminPassword"];
        if (string.IsNullOrEmpty(adminPass)
            || !DeploymentBootstrapIdentity.CredentialsMatch(request, adminUser, adminPass))
            return null;

        // The seeded administrator is NOT the deployment identity: it authenticated with
        // Auth:AdminPassword on an empty database. Carrying the deployment window here tied an
        // unrelated login to a deployment's clock, so this branch expires on its own terms.
        return new BootstrapMatch(null);
    }
}
