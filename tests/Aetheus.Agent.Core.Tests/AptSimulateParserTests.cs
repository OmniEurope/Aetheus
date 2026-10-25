// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Core.Tests;

public class AptSimulateParserTests
{
    // Realistic `apt-get -s upgrade` output: 4 upgrades, 2 from a *-security suite.
    private const string Sample = """
        NOTE: This is only a simulation!
              apt-get needs root privileges for real execution.
        Reading package lists... Done
        Building dependency tree... Done
        The following packages will be upgraded:
          curl libcurl4 openssl vim
        4 upgraded, 0 newly installed, 0 to remove and 0 not upgraded.
        Inst libcurl4 [7.81.0-1ubuntu1.15] (7.81.0-1ubuntu1.16 Ubuntu:22.04/jammy-security [amd64])
        Inst curl [7.81.0-1ubuntu1.15] (7.81.0-1ubuntu1.16 Ubuntu:22.04/jammy-security [amd64])
        Inst openssl [3.0.2-0ubuntu1.10] (3.0.2-0ubuntu1.12 Ubuntu:22.04/jammy-updates [amd64])
        Inst vim [2:8.2.3995-1ubuntu2.15] (2:8.2.3995-1ubuntu2.16 Ubuntu:22.04/jammy-updates [amd64])
        Conf libcurl4 (7.81.0-1ubuntu1.16 Ubuntu:22.04/jammy-security [amd64])
        """;

    [Fact]
    public void Parse_ExtractsAllUpgrades_WithVersions()
    {
        var updates = AptSimulateParser.Parse(Sample);

        Assert.Equal(4, updates.Count);
        var openssl = updates.Single(u => u.Package == "openssl");
        Assert.Equal("3.0.2-0ubuntu1.10", openssl.CurrentVersion);
        Assert.Equal("3.0.2-0ubuntu1.12", openssl.CandidateVersion);
    }

    [Fact]
    public void Parse_FlagsSecurityUpdates()
    {
        var updates = AptSimulateParser.Parse(Sample);

        Assert.Equal(2, updates.Count(u => u.IsSecurity)); // libcurl4 + curl
        Assert.True(updates.Single(u => u.Package == "curl").IsSecurity);
        Assert.False(updates.Single(u => u.Package == "vim").IsSecurity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Reading package lists... Done\n0 upgraded, 0 newly installed, 0 to remove and 0 not upgraded.")]
    public void Parse_NothingToUpgrade_ReturnsEmpty(string output)
        => Assert.Empty(AptSimulateParser.Parse(output));

    [Fact]
    public void BlockedPackages_FlagsCriticalFromDefaultList()
    {
        var updates = AptSimulateParser.Parse(Sample);

        var blocked = AptSimulateParser.BlockedPackages(updates);

        Assert.Contains("openssl", blocked);      // on the CriticalPackages blocklist
        Assert.DoesNotContain("vim", blocked);
        Assert.DoesNotContain("curl", blocked);
    }

    [Fact]
    public void BlockedPackages_HonorsPerServerExtraBlocklist()
    {
        var updates = AptSimulateParser.Parse(Sample);

        var blocked = AptSimulateParser.BlockedPackages(updates, ["vim"]);

        Assert.Contains("openssl", blocked); // default
        Assert.Contains("vim", blocked);     // per-server override
    }

    [Fact]
    public void BlockedPackages_NoConflicts_ReturnsEmpty()
    {
        var updates = AptSimulateParser.Parse(
            "Inst curl [1.0] (1.1 Ubuntu:22.04/jammy-updates [amd64])");

        Assert.Empty(AptSimulateParser.BlockedPackages(updates));
    }
}
