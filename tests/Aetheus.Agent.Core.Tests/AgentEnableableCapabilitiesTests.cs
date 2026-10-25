// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Tests;

/// <summary>R3-58 (ADR-051): the closed whitelist behind the UI's capability activation. Strict by design:
/// exact tokens only, so no spelling variant reaches a mapping to an installer flag.</summary>
public sealed class AgentEnableableCapabilitiesTests
{
    // ADR-055: the eleven entries, each a distinct exact token. The order is part of the contract (stable
    // normalization), and the tokens the supervisor maps are exactly these.
    private static readonly string[] Expected =
    [
        "patch-manage", "package-manage", "service-enable", "cron-manage", "rkhunter-manage", "apache-manage",
        "certbot-manage", "portsentry-manage", "firewall-manage", "mail-setup", "teamspeak-setup"
    ];

    [Fact]
    public void TheWhitelist_HoldsExactlyTheElevenAcceptedComponents()
    {
        Assert.Equal(Expected, AgentEnableableCapabilities.Whitelist);
        Assert.Equal("patch-manage", AgentEnableableCapabilities.PatchManage);
    }

    [Theory]
    [InlineData("patch-manage", "patch.manage")]
    [InlineData("package-manage", "package.manage")]
    [InlineData("service-enable", "service.manage")]
    [InlineData("cron-manage", "cron.manage")]
    [InlineData("apache-manage", "apache.manage")]
    [InlineData("certbot-manage", "certbot.manage")]
    [InlineData("portsentry-manage", "portsentry.manage")]
    [InlineData("firewall-manage", "firewall.manage")]
    [InlineData("mail-setup", "mail.manage")]
    [InlineData("teamspeak-setup", "teamspeak.manage")]
    [InlineData("rkhunter-manage", null)]
    public void EachToken_IsConfirmedByItsOwnHeartbeatCapability(string token, string? capability)
    {
        Assert.Equal(capability, AgentCapabilities.ForEnableableToken(token));
    }

    [Fact]
    public void ATokenOutsideTheWhitelist_HasNoHeartbeatCapability()
    {
        Assert.Null(AgentCapabilities.ForEnableableToken("docker"));
    }

    [Theory]
    [InlineData("package-manage")]
    [InlineData("service-enable")]
    [InlineData("cron-manage")]
    [InlineData("rkhunter-manage")]
    [InlineData("apache-manage")]
    [InlineData("certbot-manage")]
    [InlineData("portsentry-manage")]
    [InlineData("firewall-manage")]
    [InlineData("mail-setup")]
    [InlineData("teamspeak-setup")]
    public void EachAcceptedToken_IsParsedAlone(string token)
    {
        Assert.True(AgentEnableableCapabilities.TryParse(token, out var capabilities, out var rejected));
        Assert.Equal([token], capabilities);
        Assert.Null(rejected);
    }

    [Fact]
    public void AMixedRequest_IsNormalizedInWhitelistOrder()
    {
        Assert.True(AgentEnableableCapabilities.TryParse("mail-setup,patch-manage,cron-manage", out var capabilities, out _));
        Assert.Equal(["patch-manage", "cron-manage", "mail-setup"], capabilities);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoRequest_IsValidAndEmpty(string? raw)
    {
        Assert.True(AgentEnableableCapabilities.TryParse(raw, out var capabilities, out var rejected));
        Assert.Empty(capabilities);
        Assert.Null(rejected);
    }

    [Fact]
    public void TheWhitelistedToken_IsAccepted_AndDuplicatesCollapse()
    {
        Assert.True(AgentEnableableCapabilities.TryParse("patch-manage,patch-manage", out var capabilities, out _));
        Assert.Equal(["patch-manage"], capabilities);
        Assert.Equal("patch-manage", AgentEnableableCapabilities.Join(capabilities));
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("service-control")]
    [InlineData("deployment")]
    [InlineData("enable-all")]
    [InlineData("FIREWALL-MANAGE")]
    [InlineData("firewall-manage,docker")]
    [InlineData("PATCH-MANAGE")]
    [InlineData(" patch-manage")]
    [InlineData("patch-manage ")]
    [InlineData("patch-manage,")]
    [InlineData("patch-manage,docker")]
    [InlineData("patch-manage\tdocker")]
    [InlineData("--enable-all")]
    public void AnythingElse_IsRejectedByName_AndNothingIsReturned(string raw)
    {
        Assert.False(AgentEnableableCapabilities.TryParse(raw, out var capabilities, out var rejected));
        Assert.Empty(capabilities);
        Assert.NotNull(rejected);
    }

    [Fact]
    public void ANullEntry_IsRejected()
    {
        Assert.False(AgentEnableableCapabilities.TryNormalize(["patch-manage", null], out var capabilities, out var rejected));
        Assert.Empty(capabilities);
        Assert.Equal(string.Empty, rejected);
    }

    [Fact]
    public void TheAgentPublishesTheFieldItUnderstands()
    {
        Assert.Contains(AgentCapabilities.EnableCapabilitiesOnUpdate, AgentCapabilities.SoftwareCapabilities);
    }
}
