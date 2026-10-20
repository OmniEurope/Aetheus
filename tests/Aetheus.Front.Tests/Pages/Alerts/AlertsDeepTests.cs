// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using AlertsPage = Aetheus.Front.Components.Alerts.Alerts;

namespace Aetheus.Front.Tests.Pages.AlertsDeep;

public class AlertsDeepTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AlertsDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static List<AlertRuleDto> MakeAlerts() =>
    [
        new() { Id = 1, Name = "CPU High", Metric = MetricType.Cpu, Operator = ComparisonOperator.GreaterThan, Threshold = 90, SustainedSeconds = 60, IsEnabled = true, Severity = AlertSeverity.Critical, CreatedAt = DateTime.UtcNow },
        new() { Id = 2, Name = "Mem High", Metric = MetricType.Memory, Operator = ComparisonOperator.GreaterThan, Threshold = 80, SustainedSeconds = 120, IsEnabled = false, Severity = AlertSeverity.Warning, CreatedAt = DateTime.UtcNow },
        new() { Id = 3, Name = "Disk Full", Metric = MetricType.Disk, Operator = ComparisonOperator.GreaterThanOrEqual, Threshold = 95, SustainedSeconds = 300, IsEnabled = true, Severity = AlertSeverity.Info, CreatedAt = DateTime.UtcNow }
    ];

    private void SetupAlerts(List<AlertRuleDto>? list = null)
    {
        _handler.SetJsonResponse("api/alerts", list ?? MakeAlerts());
    }

    [Fact]
    public void Renders_AllAlerts()
    {
        SetupAlerts();
        var cut = Render<AlertsPage>();
        cut.WaitForState(() => cut.Markup.Contains("CPU High"));
        // All three stubbed rules are rendered in the grid.
        Assert.Contains("CPU High", cut.Markup);
        Assert.Contains("Mem High", cut.Markup);
        Assert.Contains("Disk Full", cut.Markup);
    }

    // X4D8: create/edit moved to AlertEditDialog - the page now just exposes the entry points
    // (Create button + a per-row Edit action). The form/save logic is covered by AlertEditDialogTests.
    [Fact]
    public void Renders_CreateButton()
    {
        SetupAlerts([]);
        var cut = Render<AlertsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Create"));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Create"));
    }

    [Fact]
    public void Renders_PerRowEditAction()
    {
        SetupAlerts();
        var cut = Render<AlertsPage>();
        cut.WaitForState(() => cut.Markup.Contains("CPU High"));
        // Each row exposes a pencil (edit) action that opens AlertEditDialog.
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Edit);
    }
}
