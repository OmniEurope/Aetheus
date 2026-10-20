// SPDX-License-Identifier: EUPL-1.2
using AngleSharp.Dom;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// Restoring an earlier revision of a rule from its history: the rollback is posted for that version, the
/// rules are read again from the server (no longer from the seed), the history of the rule follows the
/// refreshed rule when it still exists, and the restore is confirmed.
/// </summary>
public sealed class QualityGatePolicyEditorRollbackTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public QualityGatePolicyEditorRollbackTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static AnalysisPolicyDto Policy(int id, int version) => new()
    {
        Id = id,
        ProjectId = 4,
        PolicyKey = "project.no-cycles",
        Name = "No cycles",
        MetricKey = "architecture.cycles*",
        Operator = AnalysisPolicyOperator.GreaterThan,
        Threshold = 0,
        Scope = AnalysisPolicyScope.Project,
        IsEffective = true,
        Enabled = true,
        Version = version
    };

    private static AnalysisPolicyRevisionDto Revision(int version) => new()
    {
        PolicyId = 20,
        Version = version,
        SnapshotHash = "hash-" + version,
        CreatedAt = new DateTime(2026, 1, version, 0, 0, 0, DateTimeKind.Utc)
    };

    private IRenderedComponent<QualityGatePolicyEditor> RenderHistory()
    {
        _handler.SetJsonResponse("api/analysis/projects/4/policies/20/revisions", new List<AnalysisPolicyRevisionDto>
        {
            Revision(1), Revision(2), Revision(3)
        });
        var cut = Render<QualityGatePolicyEditor>(parameters => parameters
            .Add(component => component.Scope, AnalysisPolicyScope.Project)
            .Add(component => component.ProjectId, 4)
            .Add(component => component.CanEdit, true)
            .Add(component => component.InitialPolicies, new List<AnalysisPolicyDto> { Policy(20, 3) }));
        cut.WaitForState(() => cut.Markup.Contains("No cycles", StringComparison.Ordinal));
        cut.FindMenuItem("QualityGateHistory")!.Click();
        cut.WaitForAssertion(() => Assert.Equal(3, RestoreButtons(cut).Count));
        return cut;
    }

    private static List<IElement> RestoreButtons(IRenderedComponent<QualityGatePolicyEditor> cut) =>
        [.. cut.FindAll("button[title='Restore']")];

    private int RevisionReads() =>
        _handler.Requests.Count(request => request.Method == "GET" && request.Url.Contains("policies/20/revisions", StringComparison.Ordinal));

    [Fact]
    public void TheCurrentVersionCannotBeRestored()
    {
        var cut = RenderHistory();

        var buttons = RestoreButtons(cut);
        Assert.Single(buttons, button => button.HasAttribute("disabled"));
    }

    [Fact]
    public async Task RestoringAnEarlierVersion_PostsIt_ReadsTheRulesAgain_AndFollowsTheRefreshedRule()
    {
        var cut = RenderHistory();
        _handler.SetJsonResponse(HttpMethod.Post, "api/analysis/projects/4/policies/20/rollback/1", Policy(20, 4));
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/projects/4/policies", new List<AnalysisPolicyDto> { Policy(20, 4) });
        var readsBefore = RevisionReads();

        await RestoreButtons(cut).First(button => !button.HasAttribute("disabled")).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains(Services.Toasts(), toast => toast.Summary == "Saved"));
        Assert.Contains(_handler.Requests, request =>
            request.Method == "POST" && request.Url.EndsWith("policies/20/rollback/1", StringComparison.Ordinal));
        Assert.Contains(_handler.Requests, request =>
            request.Method == "GET" && request.Url.EndsWith("api/analysis/projects/4/policies", StringComparison.Ordinal));
        Assert.Equal(readsBefore + 1, RevisionReads());
    }

    [Fact]
    public async Task ARuleGoneAfterTheRestore_KeepsTheOldHistoryWithoutReadingItAgain()
    {
        var cut = RenderHistory();
        _handler.SetJsonResponse(HttpMethod.Post, "api/analysis/projects/4/policies/20/rollback/2", Policy(20, 4));
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/projects/4/policies", new List<AnalysisPolicyDto>());
        var readsBefore = RevisionReads();

        await RestoreButtons(cut).Last(button => !button.HasAttribute("disabled")).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains(Services.Toasts(), toast => toast.Summary == "Saved"));
        Assert.Contains(_handler.Requests, request =>
            request.Method == "POST" && request.Url.EndsWith("policies/20/rollback/2", StringComparison.Ordinal));
        Assert.Equal(readsBefore, RevisionReads());
    }
}
