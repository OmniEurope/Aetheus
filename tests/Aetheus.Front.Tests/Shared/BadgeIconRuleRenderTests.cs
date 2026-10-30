// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>The badge rule of 2026-10-25 (recette R-44 and R-59) on the shared badge components, rendered:
/// a text without number has an icon, a number has none, a one-letter grade has no badge.</summary>
public sealed class BadgeIconRuleRenderTests : BunitContext
{
    private const string IconSelector = ".omni-badge .omni-icon";

    public BadgeIconRuleRenderTests()
    {
        BunitTestHelper.RegisterServices(this);
    }

    public static TheoryData<ReleaseStatus> AllReleaseStatuses() => new(Enum.GetValues<ReleaseStatus>());

    public static TheoryData<PipelineStatus> AllRunStatuses() => new(Enum.GetValues<PipelineStatus>());

    public static TheoryData<AgentCompatibilityStatus> AllCompatibilityStatuses() => new(Enum.GetValues<AgentCompatibilityStatus>());

    public static TheoryData<AnalysisGrade> AllGrades() => new(Enum.GetValues<AnalysisGrade>());

    [Theory]
    [MemberData(nameof(AllReleaseStatuses))]
    public void ReleaseStatusBadge_CarriesAnIcon(ReleaseStatus status)
    {
        var cut = Render<ReleaseStatusBadge>(p => p.Add(x => x.Status, status));

        Assert.Single(cut.FindAll(IconSelector));
    }

    [Theory]
    [MemberData(nameof(AllRunStatuses))]
    public void PipelineRunStatusBadge_CarriesAnIcon(PipelineStatus status)
    {
        var cut = Render<PipelineRunStatusBadge>(p => p.Add(x => x.Status, status));

        Assert.Single(cut.FindAll(IconSelector));
    }

    [Theory]
    [MemberData(nameof(AllCompatibilityStatuses))]
    public void AgentCompatibilityBadge_CarriesAnIcon(AgentCompatibilityStatus status)
    {
        var cut = Render<AgentCompatibilityBadge>(p => p.Add(x => x.Value, new AgentCompatibilityDto { Status = status }));

        Assert.Single(cut.FindAll(IconSelector));
    }

    [Theory]
    [InlineData(ServerReachability.HostReachable)]
    [InlineData(ServerReachability.HostUnreachable)]
    [InlineData(ServerReachability.NotProbed)]
    public void ServerReachabilityBadge_CarriesAnIcon(ServerReachability probe)
    {
        var server = new ServerDto
        {
            Status = ServerStatus.Offline,
            LastHeartbeat = DateTime.Now.AddMinutes(-5),
            ExternalProbe = probe,
            ExternalProbeAt = DateTime.Now
        };

        var cut = Render<ServerReachabilityBadge>(p => p.Add(x => x.Server, server));

        Assert.Single(cut.FindAll(IconSelector));
    }

    [Fact]
    public void NumberBadge_HasNoIcon()
    {
        var cut = Render<NumberBadge>(p => p
            .Add(x => x.Tone, OmniTone.Success)
            .Add(x => x.Class, "mine")
            .AddChildContent("1 Online"));

        var badge = cut.Find(".omni-badge.omni-badge--success.mine");
        Assert.Equal("1 Online", badge.TextContent.Trim());
        Assert.Empty(cut.FindAll(IconSelector));
    }

    [Fact]
    public void CountBadge_HasNoIcon()
    {
        var cut = Render<CountBadge>(p => p.Add(x => x.Value, 3));

        Assert.Equal("3", cut.Find(".omni-badge").TextContent.Trim());
        Assert.Empty(cut.FindAll(IconSelector));
    }

    [Fact]
    public void StatusCountBadge_HasNoIcon_AndKeepsTheNameVisibleBesideTheCount()
    {
        var cut = Render<StatusCountBadge>(p => p
            .Add(x => x.Count, 20)
            .Add(x => x.Label, "Succeeded")
            .Add(x => x.Tone, OmniTone.Success));

        Assert.Empty(cut.FindAll(IconSelector));
        Assert.Equal("20 Succeeded", cut.Find(".omni-badge").TextContent.Trim());
    }

    [Theory]
    [MemberData(nameof(AllGrades))]
    public void GradeBadge_IsAFilledBadgeOfItsGrade_WithoutIcon(AnalysisGrade grade)
    {
        var cut = Render<GradeBadge>(p => p.Add(x => x.Value, grade).Add(x => x.Title, "Overall grade"));

        // R-107: a one-letter grade is a badge again (R-59 had been misread as "no badge"), still without icon.
        Assert.Empty(cut.FindAll(IconSelector));
        var letter = cut.Find($".omni-badge.omni-badge--solid.grade-badge.grade-badge--{grade.ToString().ToLowerInvariant()}");
        Assert.Equal(grade.ToString(), letter.TextContent.Trim());
        Assert.Equal("Overall grade", letter.GetAttribute("title"));
    }

    [Fact]
    public void GradeBadge_WithoutGrade_IsANeutralDashBadge()
    {
        var cut = Render<GradeBadge>();

        Assert.Empty(cut.FindAll(IconSelector));
        Assert.Equal("-", cut.Find(".omni-badge.grade-badge.grade-badge--none").TextContent.Trim());
    }

    [Fact]
    public void BadgeIcon_StateNeutralIsTheOffIcon_AndTheLabelNeutralIsTheTag()
    {
        Assert.Equal(OmniIconName.Prohibit, BadgeIcon.StateNameFor(OmniTone.Neutral));
        Assert.Equal(OmniIconName.Tag, BadgeIcon.NameFor(OmniTone.Neutral));
        Assert.Equal(OmniIconName.Check, BadgeIcon.NameFor(OmniTone.Success));
        Assert.Equal(OmniIconName.Close, BadgeIcon.NameFor(OmniTone.Danger));
        Assert.Equal(OmniIconName.Warning, BadgeIcon.NameFor(OmniTone.Warning));
        Assert.Equal(OmniIconName.Info, BadgeIcon.NameFor(OmniTone.Info));
    }
}
