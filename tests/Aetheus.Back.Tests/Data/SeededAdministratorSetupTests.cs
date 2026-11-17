// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.Tests.DataSeed;

/// <summary>R-1: the seeded administrator's setup is required unless the development configuration or the public
/// demo (whose administrator credential is published on purpose) turns it off.</summary>
public sealed class SeededAdministratorSetupTests
{
    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Required_ByDefault_EvenWithoutConfiguration()
    {
        Assert.True(SeededAdministratorSetup.IsRequired(null));
        Assert.True(SeededAdministratorSetup.IsRequired(Build([])));
        Assert.True(SeededAdministratorSetup.IsRequired(Build(new() { [SeededAdministratorSetup.ConfigurationKey] = "true" })));
    }

    [Fact]
    public void NotRequired_WhenTheDevelopmentSettingTurnsItOff() =>
        Assert.False(SeededAdministratorSetup.IsRequired(Build(new() { [SeededAdministratorSetup.ConfigurationKey] = "false" })));

    [Fact]
    public void NotRequired_OnThePublicDemo_OnlyWhenItIsReallyThePublicDemo()
    {
        var demo = new Dictionary<string, string?>
        {
            ["Aetheus:PublicDemo"] = "true",
            ["Seed:Demo"] = "true",
            ["Aetheus:EnvironmentTier"] = "qa"
        };
        Assert.False(SeededAdministratorSetup.IsRequired(Build(demo)));

        // A production tier claiming to be the demo keeps its setup.
        demo["Aetheus:EnvironmentTier"] = "production";
        Assert.True(SeededAdministratorSetup.IsRequired(Build(demo)));
    }
}
