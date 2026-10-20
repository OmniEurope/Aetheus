// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// PLAN-010 lot 1: the offline server shows what the backend's external probe found. The stub
/// localizer returns each key verbatim, so the rendered key proves which case was chosen.
/// </summary>
public class ServerReachabilityBadgeTests : BunitContext
{
    public ServerReachabilityBadgeTests()
    {
        BunitTestHelper.RegisterServices(this);
    }

    private static ServerDto Offline(ServerReachability probe, DateTime? probedAt, string? target = "203.0.113.5:22") => new()
    {
        Status = ServerStatus.Offline,
        LastHeartbeat = DateTime.Now.AddMinutes(-5),
        ExternalProbe = probe,
        ExternalProbeAt = probedAt,
        ExternalProbeTarget = target
    };

    [Theory]
    [InlineData(ServerReachability.HostReachable, "omni-badge--warning")]
    [InlineData(ServerReachability.HostUnreachable, "omni-badge--danger")]
    [InlineData(ServerReachability.UnknownNoAddress, "omni-badge--neutral")]
    [InlineData(ServerReachability.UnknownAddressNotAllowed, "omni-badge--neutral")]
    public void Offline_server_shows_the_probe_case_and_its_meaning(ServerReachability probe, string toneClass)
    {
        var cut = Render<ServerReachabilityBadge>(p => p
            .Add(x => x.Server, Offline(probe, DateTime.Now.AddMinutes(-4)))
            .Add(x => x.ShowDetail, true));

        Assert.Contains($"Enum_ServerReachability_{probe}", cut.Markup);
        Assert.Contains(toneClass, cut.Markup);
        var detail = cut.Find(".server-reachability-detail").TextContent;
        Assert.Contains($"ServerReachabilityHint_{probe}", detail);
        Assert.Contains("ServerReachabilityProbedTargetAgo", detail);
    }

    [Fact]
    public void Probe_older_than_the_last_heartbeat_reads_as_not_probed()
    {
        // A result from an earlier offline period must never be shown as the current state.
        var cut = Render<ServerReachabilityBadge>(p => p
            .Add(x => x.Server, Offline(ServerReachability.HostReachable, DateTime.Now.AddMinutes(-30))));

        Assert.Contains("Enum_ServerReachability_NotProbed", cut.Markup);
        Assert.DoesNotContain("Enum_ServerReachability_HostReachable", cut.Markup);
        Assert.Contains("ServerReachabilityHint_NotProbed", cut.Find(".server-reachability").GetAttribute("title"));
    }

    [Fact]
    public void Online_server_shows_nothing()
    {
        var server = Offline(ServerReachability.HostUnreachable, DateTime.Now) with { Status = ServerStatus.Online };

        var cut = Render<ServerReachabilityBadge>(p => p.Add(x => x.Server, server));

        Assert.Empty(cut.FindAll(".server-reachability"));
    }

    [Fact]
    public void Server_that_never_reported_shows_nothing()
    {
        var server = Offline(ServerReachability.NotProbed, null) with { LastHeartbeat = default };

        var cut = Render<ServerReachabilityBadge>(p => p.Add(x => x.Server, server));

        Assert.Empty(cut.FindAll(".server-reachability"));
    }

    [Theory]
    [InlineData(ServerReachability.HostReachable, "ServerOfflineToastMessage_HostReachable")]
    [InlineData(ServerReachability.HostUnreachable, "ServerOfflineToastMessage_HostUnreachable")]
    [InlineData(ServerReachability.UnknownNoAddress, "ServerOfflineToastMessage_Unknown")]
    [InlineData(ServerReachability.UnknownProbeDisabled, "ServerOfflineToastMessage_Unknown")]
    [InlineData(ServerReachability.UnknownAddressNotAllowed, "ServerOfflineToastMessage_Unknown")]
    [InlineData(ServerReachability.NotProbed, "ServerOfflineToastMessage")]
    public void Offline_alert_text_names_the_case(ServerReachability probe, string expectedKey)
    {
        Assert.Equal(expectedKey, NavMenu.OfflineToastMessageKey(probe));
    }
}
