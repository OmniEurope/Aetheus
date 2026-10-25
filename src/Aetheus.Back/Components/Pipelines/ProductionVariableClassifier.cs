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
/// (SMTP, cloud, payment) is asked, never invented.
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
        if (variable.Form is ComposeVariableForm.WithDefault or ComposeVariableForm.Alternative)
            return new(name, ProductionVariableKind.Optional, IsSecretName(name), variable.Default);
        if (IsSecretName(name))
            return new(name, OwnsItsSecret(name) ? ProductionVariableKind.GeneratedSecret : ProductionVariableKind.NeedsValue,
                true, null);

        return ConventionFor(name, slug) is { } convention
            ? new(name, ProductionVariableKind.DefaultValue, false, convention)
            : new(name, ProductionVariableKind.NeedsValue, false, null);
    }

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
