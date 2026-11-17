// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;
using Aetheus.Back.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.Tests.Configuration;

public sealed class StartupSecretsTests
{
    private static readonly string SafeJwtKey = new('j', 48);

    [Fact]
    public void ResolveJwtSigningKey_ProductionWithSafeSecrets_ReturnsConfiguredKey()
    {
        var configuration = Build(SafeSecrets());

        Assert.Equal(SafeJwtKey, StartupSecrets.ResolveJwtSigningKey(configuration, isDevelopment: false));
    }

    [Fact]
    public void ResolveJwtSigningKey_DevelopmentWithoutKey_GeneratesDistinctEphemeralKeys()
    {
        var configuration = Build([]);

        var first = StartupSecrets.ResolveJwtSigningKey(configuration, isDevelopment: true);
        var second = StartupSecrets.ResolveJwtSigningKey(configuration, isDevelopment: true);

        Assert.Equal(48, Convert.FromBase64String(first).Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ResolveJwtSigningKey_DevelopmentWithBurnedSecrets_AcceptsThem()
    {
        var configuration = Build(new()
        {
            ["Auth:JwtKey"] = "aetheus-dev-key-minimum-32-bytes!!",
            ["Auth:EncryptionKey"] = "aetheus-encryption-key-32ch!",
            ["Auth:AdminPassword"] = "admin"
        });

        Assert.Equal("aetheus-dev-key-minimum-32-bytes!!",
            StartupSecrets.ResolveJwtSigningKey(configuration, isDevelopment: true));
    }

    [Theory]
    [InlineData("Auth:JwtKey", null, "Auth:JwtKey must be configured")]
    [InlineData("Auth:JwtKey", "aetheus-dev-key-minimum-32-bytes!!", "Auth:JwtKey is set to a publicly known default value")]
    [InlineData("Auth:EncryptionKey", "aetheus-encryption-key-32ch!", "Auth:EncryptionKey is set to a publicly known default value")]
    [InlineData("Auth:EncryptionKey", null, "Auth:EncryptionKey must be configured")]
    [InlineData("Auth:EncryptionSalt", null, "Auth:EncryptionSalt must be configured")]
    [InlineData("Auth:AdminPassword", "ADMIN", "Auth:AdminPassword must not be the default 'admin' value")]
    [InlineData("Auth:AdministratorSetupRequired", "false", "Auth:AdministratorSetupRequired=false is a development setting")]
    [InlineData("Auth:AdminPassword", "aetheus-dev-admin-pwd", "Auth:AdminPassword must not be the committed development password")]
    [InlineData("Auth:AdminPassword", "AETHEUS-DEV-ADMIN-PWD", "Auth:AdminPassword must not be the committed development password")]
    [InlineData("Aetheus:PublicApiBaseUrl", null, "Aetheus:PublicApiBaseUrl must be configured")]
    [InlineData("Aetheus:PublicApiBaseUrl", "", "Aetheus:PublicApiBaseUrl must be configured")]
    [InlineData("Aetheus:PublicApiBaseUrl", "api.example.com", "Aetheus:PublicApiBaseUrl must be configured")]
    [InlineData("Aetheus:PublicApiBaseUrl", "ftp://api.example.com", "Aetheus:PublicApiBaseUrl must be configured")]
    public void ResolveJwtSigningKey_ProductionWithUnsafeSecret_Throws(string key, string? value, string expected)
    {
        var secrets = SafeSecrets();
        secrets[key] = value;

        var error = Assert.Throws<InvalidOperationException>(
            () => StartupSecrets.ResolveJwtSigningKey(Build(secrets), isDevelopment: false));

        Assert.StartsWith(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveJwtSigningKey_DevelopmentWithoutPublicApiUrlAndWithTheDevelopmentPassword_Starts()
    {
        // M5: both refusals hold outside Development only; the local instance keeps its committed settings.
        var secrets = SafeSecrets();
        secrets["Aetheus:PublicApiBaseUrl"] = null;
        secrets["Auth:AdminPassword"] = "aetheus-dev-admin-pwd";

        Assert.Equal(SafeJwtKey, StartupSecrets.ResolveJwtSigningKey(Build(secrets), isDevelopment: true));
    }

    [Theory]
    [InlineData("https://api.example.com")]
    [InlineData("http://aetheus-qa-back:8080")]
    public void ResolveJwtSigningKey_ProductionWithAnAbsolutePublicApiUrl_Starts(string url)
    {
        var secrets = SafeSecrets();
        secrets["Aetheus:PublicApiBaseUrl"] = url;

        Assert.Equal(SafeJwtKey, StartupSecrets.ResolveJwtSigningKey(Build(secrets), isDevelopment: false));
    }

    [Fact]
    public void ResolveJwtSigningKey_PublicDemoWithTheDevelopmentPassword_Starts()
    {
        // The public demo advertises its administrator password on purpose; 'admin' stays refused there.
        var secrets = SafeSecrets();
        secrets["Auth:AdminPassword"] = "aetheus-dev-admin-pwd";
        secrets["Aetheus:PublicDemo"] = "true";
        secrets["Seed:Demo"] = "true";
        secrets["Aetheus:EnvironmentTier"] = "qa";

        Assert.Equal(SafeJwtKey, StartupSecrets.ResolveJwtSigningKey(Build(secrets), isDevelopment: false));

        secrets["Auth:AdminPassword"] = "admin";
        Assert.Throws<InvalidOperationException>(() => StartupSecrets.ResolveJwtSigningKey(Build(secrets), isDevelopment: false));
    }

    [Fact]
    public void ResolveCorsOrigins_Development_ListsLocalFrontThenConfiguredOriginsSkippingEmptyOnes()
    {
        var builder = UnwatchedBuilder("Development");
        using var configuration = builder.Configuration;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cors:FrontendUrl"] = "https://front.example",
            ["Cors:Origins:0"] = "https://one.example",
            ["Cors:Origins:1"] = "",
            ["Cors:Origins:2"] = "https://front.example"
        });

        var origins = builder.ResolveCorsOrigins();

        Assert.Equal(
            [
                LocalDevelopmentEndpoints.FrontendHttpsOrigin,
                LocalDevelopmentEndpoints.FrontendHttpOrigin,
                "https://front.example",
                "https://one.example",
                "https://front.example"
            ],
            origins);
    }

    [Fact]
    public void ResolveCorsOrigins_ProductionWithoutCorsSection_IsEmpty()
    {
        var builder = UnwatchedBuilder("Production");
        using var configuration = builder.Configuration;

        Assert.Empty(builder.ResolveCorsOrigins());
    }

    // The temporary folder as content root, without the configuration file watcher: a reloadable
    // appsettings.json watched the whole temporary tree for the rest of the run, and a rename there
    // reached a watcher bug (ArgumentOutOfRangeException in PhysicalFilesWatcher.OnRenamed) that
    // crashed the test host.
    private static WebApplicationBuilder UnwatchedBuilder(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ContentRootPath = Path.GetTempPath(),
            Args = ["--hostBuilder:reloadConfigOnChange=false"]
        });
        var files = ((IConfigurationBuilder)builder.Configuration).Sources
            .OfType<Microsoft.Extensions.Configuration.FileConfigurationSource>().ToList();
        Assert.NotEmpty(files);
        Assert.DoesNotContain(files, source => source.ReloadOnChange);
        return builder;
    }

    private static Dictionary<string, string?> SafeSecrets() => new()
    {
        ["Auth:JwtKey"] = SafeJwtKey,
        ["Auth:EncryptionKey"] = new string('e', 32),
        ["Auth:EncryptionSalt"] = Convert.ToBase64String(new byte[16]),
        ["Auth:AdminPassword"] = new string('p', 20),
        ["Aetheus:PublicApiBaseUrl"] = "https://api.example.com"
    };

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
