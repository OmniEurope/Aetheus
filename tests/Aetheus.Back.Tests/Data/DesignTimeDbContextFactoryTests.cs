// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.Tests.DataSeed;

public sealed class DesignTimeDbContextFactoryTests
{
    [Fact]
    public void ResolveConnectionString_ConfiguredDefault_ReturnsValue()
    {
        var configuration = BuildConfiguration(("ConnectionStrings:Default", "Host=db;Database=aetheus"));

        Assert.Equal("Host=db;Database=aetheus", DesignTimeDbContextFactory.ResolveConnectionString(configuration));
    }

    [Fact]
    public void ResolveConnectionString_EnvironmentStyleKey_ReturnsValue()
    {
        var configuration = BuildConfiguration(("AETHEUS_DESIGN_CONNECTION", "Host=design;Database=aetheus"));

        Assert.Equal("Host=design;Database=aetheus", DesignTimeDbContextFactory.ResolveConnectionString(configuration));
    }

    [Fact]
    public void ResolveConnectionString_DesignArgument_ReturnsValueWithoutConfiguration()
    {
        var args = new[] { "--design-connection", "Host=design;Database=aetheus_bundle;Username=build" };

        var connectionString = DesignTimeDbContextFactory.ResolveConnectionString(BuildConfiguration(), args);

        Assert.Equal("Host=design;Database=aetheus_bundle;Username=build", connectionString);
    }

    [Fact]
    public void ResolveConnectionString_DesignArgument_TakesPrecedenceOverConfiguration()
    {
        var configuration = BuildConfiguration(("ConnectionStrings:Default", "Host=config;Database=aetheus"));
        var args = new[] { "--design-connection=Host=design;Database=aetheus_bundle;Username=build" };

        var connectionString = DesignTimeDbContextFactory.ResolveConnectionString(configuration, args);

        Assert.Equal("Host=design;Database=aetheus_bundle;Username=build", connectionString);
    }

    [Fact]
    public void ResolveConnectionString_MissingConfiguration_FailsWithoutCredentialFallback()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DesignTimeDbContextFactory.ResolveConnectionString(BuildConfiguration()));

        Assert.Contains("AETHEUS_DESIGN_CONNECTION", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateDbContext_MigrationBundle_OutlivesTheNpgsqlDefaultCommandTimeout()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext(
            ["--design-connection", "Host=design;Database=aetheus_bundle;Username=build"]);

        Assert.Equal(1800, context.Database.GetCommandTimeout());
    }

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value))
            .Build();
}
