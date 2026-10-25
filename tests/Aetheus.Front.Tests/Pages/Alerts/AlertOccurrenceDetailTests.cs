// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Alerts;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

/// <summary>Recette R-52: the page one alert opens on, the address the notification mail's button leads to.</summary>
public class AlertOccurrenceDetailTests : BunitContext
{
    private static readonly DateTime TriggeredAt = new(2026, 10, 22, 16, 7, 15, DateTimeKind.Utc);
    private readonly BunitTestHelper.TestHandler _handler;

    public AlertOccurrenceDetailTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_TheRuleAlert_WithItsValuesAndItsLocalTime()
    {
        _handler.SetJsonResponse("api/alerts/occurrences/42", new AlertOccurrenceDto
        {
            Id = 42,
            AlertRuleId = 5,
            RuleName = "High CPU",
            RuleIsEnabled = true,
            ServerId = 11,
            ServerName = "web-1",
            Metric = "Cpu",
            Operator = ComparisonOperator.GreaterThan,
            Threshold = 80,
            ObservedValue = 95.46,
            SustainedSeconds = 60,
            Severity = AlertSeverity.Critical,
            TriggeredAt = TriggeredAt
        });

        var cut = Render<AlertOccurrenceDetail>(parameters => parameters.Add(page => page.AlertId, 42L));

        cut.WaitForAssertion(() => Assert.Contains("High CPU", cut.Markup));
        Assert.Contains("web-1", cut.Markup);
        Assert.Contains($"{95.5}%", cut.Markup);
        Assert.Contains("&gt; 80%", cut.Markup);
        Assert.Contains("1min", cut.Markup);
        Assert.Contains(TriggeredAt.ToLocalTime().ToString("g"), cut.Markup);
        // The server is one click away: its name and the page's one main action.
        Assert.Contains("/servers/11/overview", cut.Markup);
        Assert.Contains("ViewServer", cut.Markup);
        Assert.DoesNotContain("AlertNotFound", cut.Markup);
    }

    [Fact]
    public void AMetricThisFrontDoesNotKnow_StillShowsItsNumbers_RatherThanEmptyTiles()
    {
        _handler.SetJsonResponse("api/alerts/occurrences/43", new AlertOccurrenceDto
        {
            Id = 43,
            RuleName = "Future metric",
            Metric = "SomethingNewer",
            Operator = ComparisonOperator.GreaterThan,
            Threshold = 90,
            ObservedValue = 93.44,
            Severity = AlertSeverity.Warning,
            TriggeredAt = TriggeredAt
        });

        var cut = Render<AlertOccurrenceDetail>(parameters => parameters.Add(page => page.AlertId, 43L));

        cut.WaitForAssertion(() => Assert.Contains("Future metric", cut.Markup));
        Assert.Contains($"{93.4}", cut.Markup);
        Assert.Contains("&gt; 90", cut.Markup);
    }

    [Fact]
    public void Renders_AHostCheckAlert_WithItsMessage_AndNoRuleRow()
    {
        _handler.SetJsonResponse("api/alerts/occurrences/7", new AlertOccurrenceDto
        {
            Id = 7,
            RuleName = "OOM",
            ServerId = 3,
            ServerName = "vps-1",
            Metric = "OomKill",
            Severity = AlertSeverity.Critical,
            Message = "OOM kill on vps-1: stress (pid 4242).",
            TriggeredAt = TriggeredAt
        });

        var cut = Render<AlertOccurrenceDetail>(parameters => parameters.Add(page => page.AlertId, 7L));

        cut.WaitForAssertion(() => Assert.Contains("OOM kill on vps-1: stress (pid 4242).", cut.Markup));
        Assert.Contains("vps-1", cut.Markup);
        // No measure to show, and no rule: neither the figures nor the rule's state.
        Assert.DoesNotContain("AlertMeasuredValue", cut.Markup);
        Assert.DoesNotContain("AlertRuleState", cut.Markup);
    }

    [Fact]
    public void Shows_TheNotFoundState_ForAnUnknownAlert()
    {
        _handler.SetResponse("api/alerts/occurrences/999", HttpStatusCode.NotFound);

        var cut = Render<AlertOccurrenceDetail>(parameters => parameters.Add(page => page.AlertId, 999L));

        cut.WaitForAssertion(() => Assert.Contains("AlertNotFound", cut.Markup));
        Assert.DoesNotContain("AccessDenied", cut.Markup);
    }

    [Fact]
    public void Shows_TheAccessDeniedState_WhenTheServerIsNotReadable()
    {
        _handler.SetResponse("api/alerts/occurrences/42", HttpStatusCode.Forbidden);

        var cut = Render<AlertOccurrenceDetail>(parameters => parameters.Add(page => page.AlertId, 42L));

        cut.WaitForAssertion(() => Assert.Contains("AccessDenied", cut.Markup));
        Assert.DoesNotContain("AlertNotFound", cut.Markup);
    }
}
