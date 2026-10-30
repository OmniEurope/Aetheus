// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Vaults;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>How a key of a created library or vault gets its value (recette R2-21).</summary>
internal enum RequirementKeyFill
{
    /// <summary>Left empty: only the operator knows it (a domain, a contact address).</summary>
    Empty,

    /// <summary>A convention that leaves no doubt (a sub-domain, a name derived from the project slug).</summary>
    Literal,

    /// <summary>A cryptographically random secret, drawn by <see cref="VaultSecretGenerator"/>.</summary>
    Secret
}

/// <summary>One key a generated template reads, with the rule that fills it.</summary>
internal sealed record RequirementKey(
    string Key, RequirementKeyFill Fill, string? Literal = null, int CharacterLength = 0)
{
    /// <summary>The value written at creation: the literal, a fresh secret, or empty.</summary>
    public string Value() => Fill switch
    {
        RequirementKeyFill.Literal => Literal ?? string.Empty,
        RequirementKeyFill.Secret => CharacterLength > 0
            ? VaultSecretGenerator.GenerateCharacters(CharacterLength)
            : VaultSecretGenerator.Generate(Key),
        _ => string.Empty
    };
}

/// <summary>
/// The keys of the libraries and vaults the generic templates read, named after the project
/// (<c>${{ project.slug }}.share</c>, <c>.qa</c>, <c>.prod</c>), and the rule that fills each one. A key
/// the project cannot be doubted about is filled; any other is created empty rather than guessed:
/// <list type="bullet">
/// <item><description><b>Literal</b>: the empty application sub-domain (R-74) and the sub-domain <c>api</c>, the host prefix <c>qa.</c> of
/// the QA environment, and the names derived from the slug (Compose project <c>&lt;slug&gt;-qa</c> or
/// <c>&lt;slug&gt;-prod</c>, state directory <c>/var/lib/aetheus-agent/projects/&lt;slug&gt;-production</c>, inside the directory the agent owns so it
/// creates and deletes it itself, R-97), the layout
/// ADR-053 gives the platform's own production.</description></item>
/// <item><description><b>Secret</b>: the database password, JWT signing key, encryption key and salt, with
/// the recipes of <see cref="VaultSecretGenerator"/> (the ones a deployment generates on the host), and the
/// QA smoke administrator password (24 letters and digits).</description></item>
/// <item><description><b>Empty</b>: the domain, the version suffix and the Let's Encrypt contact, which only the
/// operator knows.</description></item>
/// </list>
/// The production ports and every other variable the project's Compose file reads are not listed here: the
/// production setup reads that file and creates what it asks (ADR-057), and no environment file path exists any
/// more, the deployment renders its own from the vault and the libraries.
/// </summary>
internal static class PipelineRequirementKeys
{
    private const int SmokePasswordLength = 24;

    /// <summary>The keys of the library <paramref name="name"/>, or none when it is not one the
    /// generic templates read for this project (slug).</summary>
    public static IReadOnlyList<RequirementKey> ForLibrary(string name, string? slug)
    {
        if (slug is null) return [];
        if (Is(name, slug, "share"))
            return
            [
                // R-74: the application answers on the domain itself by default; the host formulas add the dot only for a
                // sub-domain that is not empty. The API keeps its own sub-domain.
                new("APP_SUBDOMAIN", RequirementKeyFill.Literal, string.Empty),
                new("API_SUBDOMAIN", RequirementKeyFill.Literal, "api"),
                new("CERTBOT_EMAIL", RequirementKeyFill.Empty),
                // Where the HTTP virtual hosts serve the ACME challenge: the web root the agent installer creates.
                new("ACME_WEBROOT", RequirementKeyFill.Literal, "/var/www/aetheus-acme")
            ];
        if (Is(name, slug, "qa"))
            return
            [
                new("DOMAIN", RequirementKeyFill.Empty),
                new("HOST_PREFIX", RequirementKeyFill.Literal, "qa."),
                new("COMPOSE_PROJECT", RequirementKeyFill.Literal, $"{slug}-qa")
            ];
        if (Is(name, slug, "prod"))
            return
            [
                new("DOMAIN", RequirementKeyFill.Empty),
                new("HOST_PREFIX", RequirementKeyFill.Literal, string.Empty),
                new("VERSION_SUFFIX", RequirementKeyFill.Empty),
                new("COMPOSE_PROJECT", RequirementKeyFill.Literal, $"{slug}-prod"),
                new("STATE_DIR", RequirementKeyFill.Literal, ProjectServerResourceNames.ProjectStateDirectory(slug, "production"))
            ];
        return [];
    }

    /// <summary>The secrets of the vault <paramref name="name"/>, or none when it is not one the generic
    /// templates read for this project (slug).</summary>
    public static IReadOnlyList<RequirementKey> ForVault(string name, string? slug)
    {
        if (slug is null) return [];
        if (Is(name, slug, "qa"))
            return [.. DeploymentSecrets(), new("SMOKE_ADMIN_PASSWORD", RequirementKeyFill.Secret, CharacterLength: SmokePasswordLength)];
        if (Is(name, slug, "prod"))
            return DeploymentSecrets();
        return [];
    }

    private static RequirementKey[] DeploymentSecrets() =>
        [.. VaultSecretGenerator.Profiles.Select(profile => new RequirementKey(profile.Key, RequirementKeyFill.Secret))];

    private static bool Is(string name, string slug, string suffix) =>
        string.Equals(name, $"{slug}.{suffix}", StringComparison.OrdinalIgnoreCase);
}
