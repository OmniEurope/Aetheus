// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// Drives the editor's save interaction through the rendered form. Editing a quality gate changes what
/// blocks a release, so sending the wrong scope or the wrong id silently rewrites a different rule than
/// the one on screen.
///
/// Note for anyone extending these: a templated form does NOT submit from a click on its submit
/// button under bUnit. Use <c>cut.Find("form").Submit()</c>, the pattern the auth and analysis tests
/// already use - clicking leaves Submit unfired and every assertion then passes vacuously.
/// </summary>
public sealed class QualityGatePolicyEditorSaveTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public QualityGatePolicyEditorSaveTests() => _handler = BunitTestHelper.RegisterServices(this);

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
        Version = 2
    };

    private IRenderedComponent<QualityGatePolicyEditor> RenderEditorWithFormOpen()
    {
        var cut = Render<QualityGatePolicyEditor>(parameters => parameters
            .Add(component => component.Scope, AnalysisPolicyScope.Project)
            .Add(component => component.ProjectId, 4)
            .Add(component => component.CanEdit, true)
            .Add(component => component.InitialPolicies, new List<AnalysisPolicyDto> { Local() }));
        BunitTestHelper.Interactive(this);
        cut.WaitForState(() => cut.Markup.Contains("No cycles"));
        cut.Find("button[title='Edit']").Click();
        cut.WaitForState(() => cut.FindAll("form").Count > 0);
        // The validators read the DOM, not the pre-populated model, so the required fields have to
        // be typed for Submit to fire at all. Same values the rule already carries.
        cut.Find("input#PolicyName").Input("No cycles");
        cut.Find("input#MetricKey").Input("architecture.cycles*");
        cut.Find("input#quality-gate-threshold").Change("0");
        return cut;
    }

    private void StubASuccessfulSave()
    {
        _handler.SetJsonResponse(HttpMethod.Put, "api/analysis/projects/4/policies/20", Local());
        _handler.SetJsonResponse("api/analysis/projects/4/policies", new List<AnalysisPolicyDto> { Local() });
    }

    [Fact]
    public void ClickingEdit_OpensTheRuleForm()
    {
        var cut = RenderEditorWithFormOpen();

        Assert.NotEmpty(cut.FindAll("form"));
    }

    [Fact]
    public void SavingAnExistingRule_UpdatesThatRuleRatherThanCreatingASecond()
    {
        // A PUT on the rule's own id; a POST here would leave two rules enforcing the same metric.
        StubASuccessfulSave();
        var cut = RenderEditorWithFormOpen();

        cut.Find("form").Submit();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Method == "PUT" && r.Url.Contains("/policies/20", StringComparison.Ordinal)));
        Assert.DoesNotContain(_handler.Requests, r =>
            r.Method == "POST" && r.Url.EndsWith("/policies", StringComparison.Ordinal));
    }

    [Fact]
    public void SavingTargetsTheProjectScope_NotTheGlobalOne()
    {
        // The same editor serves global, organization and project scopes; hitting the global endpoint
        // from a project view would change the rule for every project at once.
        StubASuccessfulSave();
        var cut = RenderEditorWithFormOpen();

        cut.Find("form").Submit();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Url.Contains("projects/4/policies", StringComparison.Ordinal)));
        Assert.DoesNotContain(_handler.Requests, r =>
            r.Url.Contains("policies/global", StringComparison.Ordinal));
    }

    [Fact]
    public void AfterASuccessfulSave_TheListIsReloadedFromTheServer()
    {
        // Reloading matters: the saved rule may have been renormalized server-side, and leaving the
        // form open on stale values invites a second save that undoes the first.
        StubASuccessfulSave();
        var cut = RenderEditorWithFormOpen();

        cut.Find("form").Submit();

        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r =>
                r.Method == "GET" && r.Url.Contains("projects/4/policies", StringComparison.Ordinal)));
    }

    [Fact]
    public void WhenTheSaveIsRejected_TheFormStaysOpenSoTheEditIsNotLost()
    {
        _handler.SetResponse(HttpMethod.Put, "api/analysis/projects/4/policies/20", HttpStatusCode.BadRequest);
        var cut = RenderEditorWithFormOpen();

        cut.Find("form").Submit();

        // The PUT must actually have been attempted, otherwise this assertion is vacuous.
        cut.WaitForAssertion(
            () => Assert.Contains(_handler.Requests, r => r.Method == "PUT"));
        Assert.NotEmpty(cut.FindAll("form"));
    }

    [Fact]
    public void AnInvalidFormSendsNothing()
    {
        // This is the property that distinguishes the form's Submit from a handler wired to every
        // submit: Submit fires only when the form validates. Without it, the fix that revived this
        // form would let an incomplete rule reach the API and come back a 400.
        StubASuccessfulSave();
        var cut = RenderEditorWithFormOpen();
        cut.Find("input#PolicyName").Input(string.Empty);

        cut.Find("form").Submit();

        Assert.DoesNotContain(_handler.Requests, r => r.Method == "PUT");
    }
}
