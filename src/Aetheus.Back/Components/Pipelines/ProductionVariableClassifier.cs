// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>What the production setup does with one Compose variable, before it does it.</summary>
internal sealed record ProductionVariablePlan(
    string Name, ProductionVariableKind Kind, bool Secret, string? DefaultValue)
{
    /// <summary>The entry of the library or vault <c>prod</c> that holds the value. Library entries reach every step
    /// as environment variables, and the agent refuses a step that receives a name such as ENV (the startup file of
    /// <c>sh</c>): such a variable is held under a scoped name (DEPLOY_ENV) that the environment sample maps back.</summary>
    public string Key => AgentEnvironmentPolicy.StepSafeName(Name);
}

/// <summary>
/// Decides, for each variable the Compose file of the production deployment reads, where its value comes
/// from (ADR-057): the default deployment provides it, the port registry reserves it, a random secret is
/// generated into the vault, a convention or the file's own default fills the library, or the operator is
/// asked. A name is guessed to be an application secret only from the words of its name, and only when the
/// application owns that secret (a database password, a signing key): the credential of an external service
/// (cloud, payment) is asked, never invented. The administrator, the mail server and the certificate contact are
/// never asked at all (<see cref="IsOptionalByNature"/>, recette R-91).
/// </summary>
internal static partial class ProductionVariableClassifier
{
    /// <summary>Names the default deployment exports itself: the release, the commit, the hosts and their URLs,
    /// the SDK version of <c>global.json</c> and the entries its libraries always carry.</summary>
    internal static readonly HashSet<string> ProvidedByTheTemplate = new(StringComparer.Ordinal)
    {
        "APP_VERSION", "SOURCE_COMMIT", "DOTNET_SDK_VERSION", "FRONT_URL", "API_BASE_URL", "API_URL",
        "PUBLIC_APP_URL", "PUBLIC_API_URL", "APP_HOST", "API_HOST", "DOMAIN", "HOST_PREFIX", "COMPOSE_PROJECT",
        "STATE_DIR", "APPLICATION_CANDIDATE_VERSION"
    };

    internal const string FrontPort = "PORT_FRONT";
    internal const string BackPort = "PORT_BACK";

    /// <summary>The four ports of a blue-green Compose file: reserved like <see cref="FrontPort"/> and
    /// <see cref="BackPort"/>, and the sign that the deployment is blue-green (ADR-057).</summary>
    internal static readonly string[] BlueGreenPorts =
        ["PORT_FRONT_BLUE", "PORT_BACK_BLUE", "PORT_FRONT_GREEN", "PORT_BACK_GREEN"];

    private static readonly HashSet<string> SecretWords = new(StringComparer.Ordinal)
        { "PASSWORD", "PASSWD", "PWD", "SECRET", "TOKEN", "KEY", "SALT", "CREDENTIAL", "CREDENTIALS", "APIKEY" };

    /// <summary>The first word of a secret the application owns: its database, its own tokens.</summary>
    private static readonly HashSet<string> OwnSecretOwners = new(StringComparer.Ordinal)
    {
        "DB", "DATABASE", "POSTGRES", "POSTGRESQL", "MYSQL", "MARIADB", "MONGO", "REDIS", "JWT", "ADMIN", "ROOT",
        "ENCRYPTION", "SESSION", "COOKIE", "SIGNING", "CSRF", "HMAC", "SEED", "APP", "DATA", "DEFAULT"
    };

    [GeneratedRegex("[^A-Za-z0-9]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex NotAlphanumeric();

    /// <summary>The name of the Apache <c>Define</c> prefix of a blue-green deployment: Apache Defines are global
    /// to the host, so each project names its own, upper case and starting with a letter.</summary>
    public static string UpstreamDefine(string slug)
    {
        ArgumentNullException.ThrowIfNull(slug);
        var identifier = NotAlphanumeric().Replace(slug, "_").Trim('_').ToUpperInvariant();
        return identifier.Length > 0 && char.IsAsciiLetterUpper(identifier[0]) ? identifier : "APP_" + identifier;
    }

    public static bool DeclaresBlueGreen(IEnumerable<ComposeVariable> variables) =>
        variables.Any(variable => BlueGreenPorts.Contains(variable.Name, StringComparer.Ordinal));

    /// <summary>Recette R-89: the strategy is the operator's choice, not only a deduction from the names the Compose file reads.
    /// When it differs from the one the file declares, the ports to reserve follow it: the four colour ports for a blue-green
    /// deployment, the one published port of each service for a replacement. A service the file publishes no port for gets none.
    /// The project's Compose file must read the ports of the strategy chosen; the wizard says so when it does not.</summary>
    public static IReadOnlyList<ProductionVariablePlan> ForStrategy(IReadOnlyList<ProductionVariablePlan> plans, string strategy)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var blueGreen = string.Equals(strategy, ProductionStrategy.BlueGreen, StringComparison.Ordinal);
        if (blueGreen == plans.Any(plan => BlueGreenPorts.Contains(plan.Name, StringComparer.Ordinal))) return plans;

        var front = plans.Any(plan => plan.Name == FrontPort || plan.Name.StartsWith(FrontPort + "_", StringComparison.Ordinal));
        var back = plans.Any(plan => plan.Name == BackPort || plan.Name.StartsWith(BackPort + "_", StringComparison.Ordinal));
        var names = new List<string>();
        if (front) names.AddRange(blueGreen ? [FrontPort + "_BLUE", FrontPort + "_GREEN"] : [FrontPort]);
        if (back) names.AddRange(blueGreen ? [BackPort + "_BLUE", BackPort + "_GREEN"] : [BackPort]);
        return
        [
            .. plans.Where(plan => plan.Kind != ProductionVariableKind.Port),
            .. names.Select(name => new ProductionVariablePlan(name, ProductionVariableKind.Port, false, null))
        ];
    }

    /// <summary>The API has its own host and virtual hosts: a blue-green deployment always has one, a replacement when the
    /// Compose file publishes a back port.</summary>
    public static bool DeclaresApi(IEnumerable<ProductionVariablePlan> plans, string strategy) =>
        string.Equals(strategy, ProductionStrategy.BlueGreen, StringComparison.Ordinal)
        || plans.Any(plan => plan.Name == BackPort);

    public static IReadOnlyList<ProductionVariablePlan> Classify(IEnumerable<ComposeVariable> variables, string slug)
    {
        ArgumentNullException.ThrowIfNull(variables);
        return [.. variables.Select(variable => ClassifyOne(variable, slug))];
    }

    private static ProductionVariablePlan ClassifyOne(ComposeVariable variable, string slug)
    {
        var name = variable.Name;
        if (ProvidedByTheTemplate.Contains(name))
            return new(name, ProductionVariableKind.Provided, false, null);
        if (name is FrontPort or BackPort || BlueGreenPorts.Contains(name, StringComparer.Ordinal))
            return new(name, ProductionVariableKind.Port, false, null);
        // The file names a default, so the deployment is complete without it. A secret with a default stays
        // out of the vault: Compose fills it, and nothing is invented for a setting the file made optional.
        if (variable.Form is ComposeVariableForm.WithDefault or ComposeVariableForm.Alternative || IsOptionalByNature(name))
            return new(name, ProductionVariableKind.Optional, IsSecretName(name), variable.Default);
        if (IsSecretName(name))
            return new(name, OwnsItsSecret(name) ? ProductionVariableKind.GeneratedSecret : ProductionVariableKind.NeedsValue,
                true, null);

        return ConventionFor(name, slug) is { } convention
            ? new(name, ProductionVariableKind.DefaultValue, false, convention)
            : new(name, ProductionVariableKind.NeedsValue, false, null);
    }

    /// <summary>Recette R-91 (decision 2026-10-25): the administrator's e-mail and password, the mail server (every
    /// <c>SMTP_</c> setting) and the certificate contact may stay empty, and the deployment must succeed all the same, so
    /// the setup never asks for them, never creates an entry that would block the launch while empty and never invents a
    /// password nobody knows. An application that cannot start without one is the one to adapt, and so is a Compose file
    /// that requires one with <c>:?</c>: Compose itself stops on it, and it is written <c>${NAME:-}</c>.</summary>
    internal static bool IsOptionalByNature(string name) =>
        name is "ADMIN_EMAIL" or "ADMIN_PASSWORD" or "CERTBOT_EMAIL" || name.StartsWith("SMTP_", StringComparison.Ordinal);

    private static bool IsSecretName(string name) =>
        name.Split('_', StringSplitOptions.RemoveEmptyEntries).Any(SecretWords.Contains);

    private static bool OwnsItsSecret(string name)
    {
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 && OwnSecretOwners.Contains(words[0]);
    }

    /// <summary>A value that leaves no doubt: the application's name, the environment, the database user and
    /// name derived from the project. Anything else is the operator's to give.</summary>
    internal static string? ConventionFor(string name, string slug)
    {
        var identifier = NotAlphanumeric().Replace(slug, "_").Trim('_').ToLowerInvariant();
        return name switch
        {
            "APPNAME" or "APP_NAME" => slug,
            "ENV" or "ENVIRONMENT" or "APP_ENV" or "DEPLOY_ENV" => "prod",
            "ASPNETCORE_ENVIRONMENT" or "DOTNET_ENVIRONMENT" => "Production",
            "DB_USER" or "DATABASE_USER" or "POSTGRES_USER" or "DB_NAME" or "DATABASE_NAME" or "POSTGRES_DB" =>
                identifier.Length > 0 ? identifier : null,
            _ => null
        };
    }
}
