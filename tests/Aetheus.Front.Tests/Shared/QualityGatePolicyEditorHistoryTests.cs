// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// Drives the editor's history, duplicate and export actions. Rollback itself is NOT covered here:
/// it is only reachable from a revision row, and exercising it needs a rendered history dialog with
/// at least two revisions (recorded as a follow-up rather than asserted vacuously).
///
/// bUnit notes: a templated form does not submit from a click on its button, use
/// <c>Find("form").Submit()</c>; and its text inputs answer onchange, so use <c>.Change()</c>.
/// </summary>
public sealed class QualityGatePolicyEditorHistoryTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public QualityGatePolicyEditorHistoryTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static AnalysisPolicyDto Local() => new()
    {
        Id = 20,
        ProjectId = 4,
        PolicyKey = "project.no-cycles",
        Name = "No cycles",
        MetricKey = "architecture.cycles*",
        Operator = AnalysisPolicyOperator.GreaterThan,
        Threshold = 0,
        Scope = AnalysisPolicyScope.Project,
        IsEffective = true,
        Enabled = true,
        Version = 3
    };

    private static AnalysisPolicyRevisionDto Revision(int version) => new()
    {
        PolicyId = 20,
        Version = version,
        SnapshotHash = "hash-" + version,
        CreatedAt = new DateTime(2026, 1, version, 0, 0, 0, DateTimeKind.Utc)
    };

    private IRenderedComponent<QualityGatePolicyEditor> RenderEditor()
    {
        var cut = Render<QualityGatePolicyEditor>(parameters => parameters
            .Add(component => component.Scope, AnalysisPolicyScope.Project)
            .Add(component => component.ProjectId, 4)
            .Add(component => component.CanEdit, true)
            .Add(component => component.InitialPolicies, new List<AnalysisPolicyDto> { Local() }));
        cut.WaitForState(() => cut.Markup.Contains("No cycles"));
        return cut;
    }

    private void StubRevisions(params int[] versions) =>
        _handler.SetJsonResponse(
            "api/analysis/projects/4/policies/20/revisions",
            versions.Select(Revision).ToList());

    [Fact]
    public void OpeningHistoryLoadsTheRevisionsOfThatRule()
    {
        StubRevisions(1, 2, 3);
        var cut = RenderEditor();

        cut.FindMenuItem("QualityGateHistory")!.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Method == "GET" && r.Url.Contains("policies/20/revisions", StringComparison.Ordinal)));
    }

    [Fact]
    public void ARuleWithNoRevisionsStillOpensItsHistory()
    {
        StubRevisions();
        var cut = RenderEditor();

        cut.FindMenuItem("QualityGateHistory")!.Click();

        // Rollback is not asserted here: it is only reachable from a revision row, which an empty
        // history never renders, so asserting its absence would hold vacuously.
        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r => r.Url.Contains("revisions", StringComparison.Ordinal)));
    }

    [Fact]
    public void DuplicatingARuleOpensTheFormWithoutCallingTheApi()
    {
        // Duplicate is a local pre-fill; saving is a separate, explicit act.
        var cut = RenderEditor();
        var before = _handler.Requests.Count;

        cut.FindMenuItem("Duplicate")!.Click();

        cut.WaitForState(() => cut.FindAll("form").Count > 0);
        Assert.Equal(before, _handler.Requests.Count);
    }

    [Fact]
    public void DuplicatingThenSavingCreatesANewRule_RatherThanOverwritingTheOriginal()
    {
        // The duplicate carries no id, so it must POST. A PUT here would silently replace the rule
        // the user meant to copy.
        _handler.SetJsonResponse(HttpMethod.Post, "api/analysis/projects/4/policies", Local());
        _handler.SetJsonResponse("api/analysis/projects/4/policies", new List<AnalysisPolicyDto> { Local() });
        var cut = RenderEditor();
        cut.FindMenuItem("Duplicate")!.Click();
        cut.WaitForState(() => cut.FindAll("form").Count > 0);
        cut.Find("input#PolicyName").Input("No cycles copy");
        cut.Find("input#MetricKey").Input("architecture.cycles*");
        cut.Find("input#quality-gate-threshold").Change("0");

        cut.Find("form").Submit();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Method == "POST" && r.Url.EndsWith("projects/4/policies", StringComparison.Ordinal)));
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "PUT");
    }

    [Fact]
    public void ExportingWritesAFileThroughJsInterop_WithoutTouchingTheApi()
    {
        // Export is a client-side dump of what is already loaded; a round trip would risk exporting
        // something other than what the user is looking at.
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = RenderEditor();
        var before = _handler.Requests.Count;

        cut.FindMenuItem("Export")!.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "downloadFile"));
        Assert.Equal(before, _handler.Requests.Count);
    }

    [Fact]
    public void TheExportedFileIsNamedForTheScopeItCameFrom()
    {
        // Exporting a project gate and a global gate to the same filename would let one overwrite the
        // other in the user's downloads.
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = RenderEditor();

        cut.FindMenuItem("Export")!.Click();

        cut.WaitForAssertion(() =>
        {
            var call = JSInterop.Invocations.First(i => i.Identifier == "downloadFile");
            Assert.Equal("quality-gates-project.json", call.Arguments[0]);
        });
    }

    [Fact]
    public void TheExportedPayloadIsJson_AndCarriesTheRules()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = RenderEditor();

        cut.FindMenuItem("Export")!.Click();

        cut.WaitForAssertion(() =>
        {
            var call = JSInterop.Invocations.First(i => i.Identifier == "downloadFile");
            Assert.Equal("application/json", call.Arguments[2]);
            Assert.Contains("architecture.cycles*", (string)call.Arguments[1]!, StringComparison.Ordinal);
        });
    }
}
