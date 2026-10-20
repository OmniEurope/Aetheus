// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Resources;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Resources;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// PLAN-010 lot 1: the overview states the agent's clock offset with the bound of its measure, says
/// no direction when the bound covers zero, and reads an unmeasured clock as unknown. The texts come
/// from the real English resources so the test sees what an operator reads.
/// </summary>
public sealed class ServerOverviewClockOffsetTests : BunitContext
{
    public ServerOverviewClockOffsetTests()
    {
        BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<IStringLocalizer<AppStrings>>(new EnglishResourceLocalizer());
        Services.GetRequiredService<PermissionService>().SetPermissions([], isAdmin: true);
    }

    private string ClockText(double? offsetMs, double? uncertaintyMs)
    {
        var cut = Render<ServerOverviewSection>(p => p
            .Add(x => x.Server, new ServerDetailDto
            {
                Id = 1,
                Name = "vps",
                Hostname = "vps.local",
                Type = ServerType.Normal,
                Status = ServerStatus.Online,
                Tags = [],
                Services = [],
                ClockOffsetMs = offsetMs,
                ClockOffsetUncertaintyMs = uncertaintyMs
            })
            .Add(x => x.MetricsReceived, true));
        var label = cut.FindAll(".essential-label").Single(element => element.TextContent == "Clock offset");
        return label.ParentElement!.QuerySelector(".essential-value")!.TextContent.Trim();
    }

    private static string Seconds(double value) => value.ToString("0.0", CultureInfo.CurrentCulture) + " s";

    [Fact]
    public void AgentAhead_IsStatedWithItsBound()
    {
        Assert.Equal($"{Seconds(2.5)} ahead (± 40 ms)", ClockText(2_500, 40));
    }

    [Fact]
    public void AgentBehind_IsStatedWithItsBound()
    {
        Assert.Equal($"{Seconds(12.3)} behind (± {Seconds(1.2)})", ClockText(-12_300, 1_200));
    }

    [Fact]
    public void OffsetWithinTheBound_ClaimsNoDirection()
    {
        Assert.Equal("In sync (± 15 ms)", ClockText(-8, 15));
    }

    [Fact]
    public void UnmeasuredClock_ReadsAsUnknown_NotInSync()
    {
        Assert.Equal("Unknown", ClockText(null, null));
    }

    private sealed class EnglishResourceLocalizer : IStringLocalizer<AppStrings>
    {
        private static readonly ResourceManager Resources =
            new("Aetheus.Front.Resources.AppStrings", typeof(AppStrings).Assembly);

        public LocalizedString this[string name] =>
            Resources.GetString(name, CultureInfo.InvariantCulture) is { } value
                ? new LocalizedString(name, value)
                : new LocalizedString(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(CultureInfo.CurrentCulture, this[name].Value, arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
