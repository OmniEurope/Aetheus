// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Resources;
using Microsoft.Extensions.Localization;
using NSubstitute;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>The hosting server column names the deduced fleet server of a backend-probed app, and
/// links the registered or deduced server.</summary>
public sealed class MonitoredAppServerLabelTests
{
    private static IStringLocalizer<AppStrings> Localizer()
    {
        var localizer = Substitute.For<IStringLocalizer<AppStrings>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(call =>
            new LocalizedString(call.ArgAt<string>(0), $"{call.ArgAt<string>(0)}:{string.Join(",", call.ArgAt<object[]>(1))}"));
        return localizer;
    }

    [Fact]
    public void RegisteredServer_IsNamedAndLinked()
    {
        var app = new MonitoredAppDto { ServerId = 9, ServerName = "web-09" };

        Assert.Equal("web-09", MonitoredAppServerLabel.Format(app, Localizer()));
        Assert.Equal(9, MonitoredAppServerLabel.ServerId(app));
    }

    [Fact]
    public void BackendProbedAppOnAFleetServer_NamesThatServerAndTheBackendProbe()
    {
        var app = new MonitoredAppDto { HostingServer = new MonitoredAppHostingServerDto { ServerId = 7, ServerName = "vps2577917" } };

        Assert.Equal("HostedOnServerBackendProbe:vps2577917", MonitoredAppServerLabel.Format(app, Localizer()));
        Assert.Equal(7, MonitoredAppServerLabel.ServerId(app));
    }

    [Fact]
    public void AppWithNoKnownServer_StaysOffFleet()
    {
        var app = new MonitoredAppDto();

        Assert.Equal("OffFleetBackendProbe", MonitoredAppServerLabel.Format(app, Localizer()));
        Assert.Null(MonitoredAppServerLabel.ServerId(app));
    }
}
