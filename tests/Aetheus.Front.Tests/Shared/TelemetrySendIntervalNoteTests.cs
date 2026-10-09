// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// The send interval, stated where an application's telemetry is read: the server's interval with its
/// source, what a chart point stands for, and the interval actually observed when the application does
/// not follow the server's (an Aetheus.Telemetry package older than 1.1.0).
/// </summary>
public sealed class TelemetrySendIntervalNoteTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public TelemetrySendIntervalNoteTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.French());
    }

    private IRenderedComponent<TelemetrySendIntervalNote> RenderNote(int? observed, bool hourly = false)
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/4/send-interval", new AppTelemetrySendIntervalDto
        {
            ServerIntervalSeconds = 300,
            Profile = DataRetentionProfile.Standard,
            ObservedIntervalSeconds = observed
        });
        return Render<TelemetrySendIntervalNote>(parameters => parameters
            .Add(note => note.AppId, 4)
            .Add(note => note.HourlyPoints, hourly));
    }

    [Fact]
    public void AnApplicationThatFollowsTheServer_ShowsTheIntervalAndNothingElse()
    {
        var cut = RenderNote(observed: 300);

        cut.WaitForAssertion(() => Assert.Equal(
            "Envoi toutes les 5 min (profil Standard) : un point toutes les 5 minutes",
            cut.Find("[data-testid=telemetry-send-interval-setting]").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-testid=telemetry-send-interval-observation]"));
        Assert.DoesNotContain("un point par heure", cut.Find("[data-testid=telemetry-send-interval]").TextContent);
    }

    [Fact]
    public void AnOldPackage_IsAWarning_WithTheIntervalItActuallySends()
    {
        var cut = RenderNote(observed: 60);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=telemetry-send-interval-observation]")));
        var observation = cut.Find("[data-testid=telemetry-send-interval-observation]");
        Assert.Contains("omni-alert", observation.ClassName ?? string.Empty);
        Assert.Contains("Intervalle observé : un point par minute.", observation.TextContent);
        Assert.Contains("Aetheus.Telemetry 1.1.0", observation.TextContent);
    }

    [Fact]
    public void WithoutAnObservedInterval_TheVersionThatAppliesTheServersIsNamed()
    {
        var cut = RenderNote(observed: null);

        cut.WaitForAssertion(() => Assert.Contains(
            "ne s'applique qu'à partir d'Aetheus.Telemetry 1.1.0",
            cut.Find("[data-testid=telemetry-send-interval-observation]").TextContent));
    }

    [Fact]
    public void BeyondTwentyFourHours_TheChartPointIsAnHour()
    {
        var cut = RenderNote(observed: 300, hourly: true);

        cut.WaitForAssertion(() => Assert.Contains(
            "Au-delà de 24 heures, le graphique montre un point par heure (moyenne horaire).",
            cut.Find("[data-testid=telemetry-send-interval]").TextContent));
    }

    [Fact]
    public void AFailedCall_ShowsNoNote_AndBreaksNothing()
    {
        _handler.SetResponse("api/appmonitoring/apps/4/send-interval", HttpStatusCode.InternalServerError);

        var cut = Render<TelemetrySendIntervalNote>(parameters => parameters.Add(note => note.AppId, 4));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Url.Contains("send-interval")));
        Assert.Empty(cut.FindAll("[data-testid=telemetry-send-interval]"));
    }
}
