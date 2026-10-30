// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>The detection mode PortSentry reports ("atcp") is said in words; an unknown or empty mode is not shown.
/// The test host's localizer answers each key with itself, so the text names the resource that was chosen.</summary>
public sealed class ServerPortsentryModeTextTests : BunitContext
{
    private readonly ServerPortsentrySection _section;

    public ServerPortsentryModeTextTests()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse("portsentry/filter-values", new PortsentryFilterValuesDto());
        handler.SetJsonResponse("api/servers/50/portsentry/whitelist", new PaginatedResult<PortsentryWhitelistIpDto>());
        handler.SetJsonResponse("api/servers/50/portsentry/blocked", new PaginatedResult<PortsentryBlockedIpDto>());
        _section = Render<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 50)
            .Add(x => x.Ps, new PortsentryDataDto { IsInstalled = true, IsRunning = true, Mode = "tcp" })).Instance;
    }

    [Theory]
    [InlineData("tcp")]
    [InlineData("udp")]
    [InlineData("stcp")]
    [InlineData("sudp")]
    [InlineData("atcp")]
    [InlineData("audp")]
    public void AKnownMode_IsSaidWithItsOwnResource(string mode) =>
        Assert.Equal($"PortsentryMode_{mode}", _section.PortsentryModeText(mode));

    [Theory]
    [InlineData("ATCP", "atcp")]
    [InlineData("  Stcp ", "stcp")]
    public void TheModeIsTrimmedAndCaseInsensitive(string reported, string resource) =>
        Assert.Equal($"PortsentryMode_{resource}", _section.PortsentryModeText(reported));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xtcp")]
    [InlineData("tcp,udp")]
    public void AnUnknownOrEmptyMode_IsNotShown(string? mode) =>
        Assert.Null(_section.PortsentryModeText(mode));
}
