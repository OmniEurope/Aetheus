// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Tests;

/// <summary>R3-58 (ADR-051): the closed whitelist behind the UI's capability activation. Strict by design:
/// exact tokens only, so no spelling variant reaches a mapping to an installer flag.</summary>
public sealed class AgentEnableableCapabilitiesTests
{
    [Fact]
    public void TheWhitelist_HoldsExactlyPatchManage()
    {
        Assert.Equal([AgentEnableableCapabilities.PatchManage], AgentEnableableCapabilities.Whitelist);
        Assert.Equal("patch-manage", AgentEnableableCapabilities.PatchManage);
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
    [InlineData("firewall-manage")]
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
