// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Reading the variables of a Compose file and deciding where each value comes from (ADR-057).</summary>
public sealed class ComposeVariableTests
{
    // The shape of a real production Compose file: required variables with and without a message, defaults,
    // a variable repeated with and without its guard, an escaped dollar and comments naming variables.
    private const string Compose = """
        # Remote deployment: exports ${NOT_READ_FROM_A_COMMENT} from the server's .env file.
        services:
          front:
            container_name: ${APPNAME:?}-${ENV:?}-front
            build:
              args:
                DOTNET_SDK_VERSION: ${DOTNET_SDK_VERSION:?DOTNET_SDK_VERSION comes from global.json}
            ports:
              - "127.0.0.1:${PORT_FRONT:?}:80"
            environment:
              - API_BASE_URL=${API_BASE_URL:?}
          back:
            ports:
              - "127.0.0.1:${PORT_BACK:?}:8080"
            environment:
              - ConnectionStrings__DefaultConnection=Host=db;Database=${APPNAME};Username=${DB_USER:?};Password=${DB_PASSWORD:?DB_PASSWORD is required}
              - Jwt__SigningKey=${JWT_KEY:?JWT_KEY is required}
              - Seed__AdminEmail=${ADMIN_EMAIL:?ADMIN_EMAIL is required}
              - Seed__AdminPassword=${ADMIN_PASSWORD:?}
              - Seed__OwnerEmail=${OWNER_EMAIL:?OWNER_EMAIL is required}
              - Smtp__Host=${SMTP_HOST:?SMTP_HOST is required}
              - Smtp__From=${SMTP_FROM:?SMTP_FROM is required}
              - Certbot__Email=${CERTBOT_EMAIL:?}
              - Smtp__Port=${SMTP_PORT:-587}
              - Smtp__Password=${SMTP_PASSWORD:-}
              - Smtp__Credential=${SMTP_CREDENTIAL:?}
              - Payment__Key=${PAYMENT_API_KEY:?}
              - Release=$APP_VERSION
          db:
            healthcheck:
              test: ["CMD-SHELL", "pg_isready -U ${DB_USER} -d $${POSTGRES_DB} -h $$HOSTNAME"]
        """;

    private static ComposeVariable Variable(string name) =>
        ComposeVariableExtractor.Extract(Compose).Single(variable => variable.Name == name);

    [Fact]
    public void EveryVariableTheFileReads_IsListedOnce_AndNothingFromCommentsOrEscapedDollars()
    {
        var names = ComposeVariableExtractor.Extract(Compose).Select(variable => variable.Name).ToList();

        Assert.Equal(
            [
                "APPNAME", "ENV", "DOTNET_SDK_VERSION", "PORT_FRONT", "API_BASE_URL", "PORT_BACK", "DB_USER",
                "DB_PASSWORD", "JWT_KEY", "ADMIN_EMAIL", "ADMIN_PASSWORD", "OWNER_EMAIL", "SMTP_HOST", "SMTP_FROM",
                "CERTBOT_EMAIL", "SMTP_PORT", "SMTP_PASSWORD", "SMTP_CREDENTIAL", "PAYMENT_API_KEY", "APP_VERSION"
            ],
            names);
        Assert.DoesNotContain("NOT_READ_FROM_A_COMMENT", names);
        Assert.DoesNotContain("POSTGRES_DB", names);
        Assert.DoesNotContain("HOSTNAME", names);
    }

    [Fact]
    public void ARequiredGuard_WinsOverAPlainUse_AndADefaultIsKept()
    {
        Assert.Equal(ComposeVariableForm.Required, Variable("APPNAME").Form);
        Assert.Equal(ComposeVariableForm.Required, Variable("DB_USER").Form);
        Assert.Equal(ComposeVariableForm.Plain, Variable("APP_VERSION").Form);
        Assert.Equal(ComposeVariableForm.WithDefault, Variable("SMTP_PORT").Form);
        Assert.Equal("587", Variable("SMTP_PORT").Default);
        Assert.Equal(string.Empty, Variable("SMTP_PASSWORD").Default);
    }

    private static Dictionary<string, ProductionVariablePlan> Plans(string slug = "wordmorrow") =>
        ProductionVariableClassifier.Classify(ComposeVariableExtractor.Extract(Compose), slug)
            .ToDictionary(plan => plan.Name, StringComparer.Ordinal);

    [Theory]
    [InlineData("APP_VERSION")]
    [InlineData("DOTNET_SDK_VERSION")]
    [InlineData("API_BASE_URL")]
    public void WhatTheDefaultDeploymentProvides_IsNotCreated(string name) =>
        Assert.Equal(ProductionVariableKind.Provided, Plans()[name].Kind);

    [Theory]
    [InlineData("PORT_FRONT")]
    [InlineData("PORT_BACK")]
    public void TheFrontAndBackPorts_AreReservedInThePortRegistry(string name) =>
        Assert.Equal(ProductionVariableKind.Port, Plans()[name].Kind);

    [Theory]
    [InlineData("DB_PASSWORD")]
    [InlineData("JWT_KEY")]
    public void ASecretTheApplicationOwns_IsGeneratedIntoTheVault(string name)
    {
        var plan = Plans()[name];

        Assert.Equal(ProductionVariableKind.GeneratedSecret, plan.Kind);
        Assert.True(plan.Secret);
        Assert.Null(plan.DefaultValue);
    }

    [Fact]
    public void TheCredentialOfAnExternalService_IsAskedNeverInvented()
    {
        var plan = Plans()["PAYMENT_API_KEY"];

        Assert.Equal(ProductionVariableKind.NeedsValue, plan.Kind);
        Assert.True(plan.Secret);
    }

    [Fact]
    public void ASettingWithAConvention_GetsItsValue_AndAnotherIsAsked()
    {
        var plans = Plans();

        Assert.Equal("wordmorrow", plans["APPNAME"].DefaultValue);
        Assert.Equal("prod", plans["ENV"].DefaultValue);
        Assert.Equal("wordmorrow", plans["DB_USER"].DefaultValue);
        Assert.Equal(ProductionVariableKind.DefaultValue, plans["DB_USER"].Kind);
        Assert.Equal(ProductionVariableKind.NeedsValue, plans["OWNER_EMAIL"].Kind);
        Assert.False(plans["OWNER_EMAIL"].Secret);
    }

    /// <summary>Recette R-91 (decision 2026-10-25): the administrator, the mail server and the certificate contact may stay
    /// empty and the deployment must succeed anyway, so the wizard neither asks for them, nor creates an entry that would block
    /// the launch while empty, nor invents a password nobody knows. A Compose file that requires one of them with
    /// <c>:?</c> still stops in Compose itself: the project's file is the one to adapt (<c>${NAME:-}</c>).</summary>
    [Theory]
    [InlineData("ADMIN_EMAIL", false)]
    [InlineData("ADMIN_PASSWORD", true)]
    [InlineData("SMTP_HOST", false)]
    [InlineData("SMTP_FROM", false)]
    [InlineData("SMTP_CREDENTIAL", true)]
    [InlineData("CERTBOT_EMAIL", false)]
    public void AValueTheOperatorMayLeaveEmpty_IsOptional_NeverAskedCreatedOrInvented(string name, bool secret)
    {
        var plan = Plans()[name];

        Assert.Equal(ProductionVariableKind.Optional, plan.Kind);
        Assert.Equal(secret, plan.Secret);
        Assert.False(ProductionEnvSampleBuilder.Supplied(plan));
    }

    [Fact]
    public void AVariableTheFileDefaults_IsOptional_AndNothingIsCreatedForIt()
    {
        var plans = Plans();

        Assert.Equal(ProductionVariableKind.Optional, plans["SMTP_PORT"].Kind);
        Assert.Equal("587", plans["SMTP_PORT"].DefaultValue);
        Assert.Equal(ProductionVariableKind.Optional, plans["SMTP_PASSWORD"].Kind);
    }

    [Fact]
    public void TheEnvSample_NamesEveryEntryTheVaultOrTheLibrarySupplies_AndNothingElse()
    {
        var sample = ProductionEnvSampleBuilder.Build(Plans().Values);
        var lines = sample.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith('#')).ToList();

        Assert.Equal(
            [
                "APPNAME=#{APPNAME}#", "DB_PASSWORD=#{DB_PASSWORD}#", "DB_USER=#{DB_USER}#", "ENV=#{DEPLOY_ENV}#",
                "JWT_KEY=#{JWT_KEY}#", "OWNER_EMAIL=#{OWNER_EMAIL}#", "PAYMENT_API_KEY=#{PAYMENT_API_KEY}#"
            ],
            lines);
        Assert.DoesNotContain("PORT_FRONT", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("SMTP_PORT", sample, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryVariableTheFileRequires_ReachesCompose_ThroughTheRenderedFileTheLibrariesOrTheDeploymentItself()
    {
        // Compose stops on a required variable nobody gives: the rendered file, the prod library (ports, which reach
        // Compose through the process environment) or the deployment itself must give every one of them.
        var sampleNames = ProductionEnvSampleBuilder.Build(Plans().Values)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith('#'))
            .Select(line => line.Split('=', 2)[0]).ToHashSet(StringComparer.Ordinal);

        foreach (var required in ComposeVariableExtractor.Extract(Compose).Where(variable => variable.Form == ComposeVariableForm.Required))
        {
            var plan = Plans()[required.Name];
            // The only exception is the user's decision of R-91: a value that may stay empty (administrator, mail server,
            // certificate contact) is supplied by nobody, and the project's Compose file is the one to write `${NAME:-}`.
            var supplied = sampleNames.Contains(required.Name)
                || plan.Kind is ProductionVariableKind.Provided or ProductionVariableKind.Port
                || (plan.Kind == ProductionVariableKind.Optional && ProductionVariableClassifier.IsOptionalByNature(required.Name));
            Assert.True(supplied, $"{required.Name} is required by the Compose file and reaches it from nowhere.");
        }
    }

    [Fact]
    public void TheEnvSample_ContainsNoValue_SoItCanBeVersioned()
    {
        // Every line is NAME=#{KEY}#: the sample is a list of names, rendered against the vault at deployment. The key
        // is the name itself, or its scoped form when the agent refuses the name as a step variable (ENV).
        var values = ProductionEnvSampleBuilder.Build(Plans().Values)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith('#'))
            .Select(line => line.Split('=', 2));

        Assert.All(values, pair => Assert.Equal($"#{{{AgentEnvironmentPolicy.StepSafeName(pair[0])}}}#", pair[1]));
    }

    [Fact]
    public void ABlueGreenComposeFile_IsDetectedFromItsPorts()
    {
        var blueGreen = ComposeVariableExtractor.Extract("ports: ['${PORT_FRONT_BLUE:?}:80', '${PORT_FRONT_GREEN:?}:80']");

        Assert.True(ProductionVariableClassifier.DeclaresBlueGreen(blueGreen));
        Assert.False(ProductionVariableClassifier.DeclaresBlueGreen(ComposeVariableExtractor.Extract(Compose)));
    }
}
