// SPDX-License-Identifier: EUPL-1.2
using AngleSharp.Dom;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// Drives the editor's bulk actions: applying a preset, previewing a rule before saving it, and
/// exporting. A preset rewrites the whole gate in one call, so sending it to the wrong scope or losing
/// the id of an existing rule would either duplicate rules or silently retarget another project.
///
/// bUnit notes: a templated form does not submit from a click on its button, use
/// <c>Find("form").Submit()</c>; and its text inputs answer onchange, so use <c>.Change()</c>.
/// </summary>
public sealed class QualityGatePolicyEditorPresetTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public QualityGatePolicyEditorPresetTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static AnalysisPolicyDto Local(int id = 20, string key = "project.no-cycles") => new()
    {
        Id = id,
        ProjectId = 4,
        PolicyKey = key,
        Name = "No cycles",
        MetricKey = "architecture.cycles*",
        Operator = AnalysisPolicyOperator.GreaterThan,
        Threshold = 0,
        Scope = AnalysisPolicyScope.Project,
        IsEffective = true,
        Enabled = true,
        Version = 2
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

    private void StubBatchAndReload()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/analysis/projects/4/policies/batch", new List<AnalysisPolicyDto> { Local() });
        _handler.SetJsonResponse(
            "api/analysis/projects/4/policies", new List<AnalysisPolicyDto> { Local() });
    }

    private static IElement Button(IRenderedComponent<QualityGatePolicyEditor> cut, string label) =>
        cut.FindAll("button").First(b => b.Names().Contains(label, StringComparison.Ordinal));

    [Fact]
    public void ApplyingTheRecommendedPreset_SendsOneBatchToTheProjectScope()
    {
        // A preset is a single batch call; sending rule-by-rule would leave the gate half-applied if
        // one call failed.
        StubBatchAndReload();
        var cut = RenderEditor();

        cut.FindMenuItem("QualityGateRecommended")!.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Method == "POST" && r.Url.EndsWith("projects/4/policies/batch", StringComparison.Ordinal)));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("policies/global", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyingTheStrictPreset_AlsoGoesThroughTheBatchEndpoint()
    {
        StubBatchAndReload();
        var cut = RenderEditor();

        cut.FindMenuItem("QualityGateStrict")!.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Method == "POST" && r.Url.EndsWith("projects/4/policies/batch", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheStrictAndRecommendedPresetsDoNotSendTheSamePayload()
    {
        // If they did, one of the two buttons would be a lie.
        StubBatchAndReload();

        var recommended = RenderEditor();
        recommended.FindMenuItem("QualityGateRecommended")!.Click();
        recommended.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r => r.Method == "POST"));
        var recommendedBody = _handler.RequestDetails.Last(r => r.Method == "POST").Body;

        var strict = RenderEditor();
        strict.FindMenuItem("QualityGateStrict")!.Click();
        strict.WaitForAssertion(
            () => Assert.True(_handler.RequestDetails.Count(r => r.Method == "POST") >= 2));
        var strictBody = _handler.RequestDetails.Last(r => r.Method == "POST").Body;

        Assert.NotEqual(recommendedBody, strictBody);
    }

    [Fact]
    public void ApplyingAPresetReloadsTheListFromTheServer()
    {
        // The server normalizes what it stored; keeping the local copy would show rules that differ
        // from what actually enforces.
        StubBatchAndReload();
        var cut = RenderEditor();

        cut.FindMenuItem("QualityGateRecommended")!.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Method == "GET" && r.Url.Contains("projects/4/policies", StringComparison.Ordinal)));
    }

    [Fact]
    public void ARejectedPresetDoesNotReloadTheList()
    {
        // Reloading after a failure would replace what the user sees with the unchanged server state
        // and hide the fact that nothing was applied.
        _handler.SetResponse(
            HttpMethod.Post, "api/analysis/projects/4/policies/batch", System.Net.HttpStatusCode.BadRequest);
        var cut = RenderEditor();

        cut.FindMenuItem("QualityGateRecommended")!.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r => r.Method == "POST"));
        Assert.DoesNotContain(_handler.Requests, r =>
            r.Method == "GET" && r.Url.Contains("projects/4/policies", StringComparison.Ordinal));
    }

    [Fact]
    public void PreviewingARuleSendsItToThePreviewEndpoint_WithoutSavingIt()
    {
        // Preview is how a maintainer checks a threshold before committing to it; if it saved, the
        // check would be the change.
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/analysis/projects/4/policies/preview", new AnalysisPolicySetPreviewDto());
        var cut = RenderEditor();
        cut.Find("button[title='Edit']").Click();
        cut.WaitForState(() => cut.FindAll("form").Count > 0);

        cut.FindMenuItem("QualityGatePreview")!.Click();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Method == "POST" && r.Url.EndsWith("policies/preview", StringComparison.Ordinal)));
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "PUT");
    }

    [Fact]
    public void CancellingTheFormCallsNothing()
    {
        var cut = RenderEditor();
        cut.Find("button[title='Edit']").Click();
        cut.WaitForState(() => cut.FindAll("form").Count > 0);
        var before = _handler.Requests.Count;

        Button(cut, "GoBack").Click();

        // The point of Cancel is that the edit is discarded locally; it must never reach the API.
        Assert.Equal(before, _handler.Requests.Count);
    }
}
