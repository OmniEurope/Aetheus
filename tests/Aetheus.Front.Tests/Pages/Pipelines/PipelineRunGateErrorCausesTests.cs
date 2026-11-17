// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-21: the Gate tab of run 3100 (nightly 3097) read "Error" beside a grade A with no finding and said
/// nothing of which producer or report was refused. An Error Gate now lists each cause and shows no letter.</summary>
public sealed class PipelineRunGateErrorCausesTests : BunitContext
{
    private static readonly IStringLocalizer<Aetheus.Front.Resources.AppStrings> Localizer = new FormatLocalizer(
        new Dictionary<string, string>
        {
            ["GateErrorCauseReportTimedOut"] = "{0} : l'outil a dépassé son délai.",
            ["GateErrorCauseDependencyTrack"] = "{0} : envoi du SBOM non terminé (état : {1}).",
            ["GateErrorCauseDetail"] = "Détail : {0}",
            ["GateErrorCauseStep"] = "étape « {0} »",
            ["GateErrorCauseReport"] = "rapport #{0}",
            ["GateErrorCauseArtifact"] = "artefact #{0}",
            ["GateErrorCauseFile"] = "fichier {0}",
            ["GateErrorCauseRun"] = "run {0}"
        });

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunGateErrorCausesTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static AnalysisRunGateDto ErrorGateWithAGoodLetter() => new()
    {
        PipelineRunId = 3100,
        Status = AnalysisGateStatus.Error,
        ReportCount = 9,
        Grade = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.A, Completeness = AnalysisGradeCompleteness.Complete },
        ErrorCauses =
        [
            new AnalysisGateErrorCauseDto
            {
                Kind = AnalysisGateErrorCauseKind.ReportTimedOut,
                PipelineRunId = 3100,
                ScannerKey = "trivy-dependencies",
                ScannerName = "Trivy",
                StepName = "Scan dependencies",
                ReportId = 4,
                PipelineArtifactId = 7,
                ReportPath = ".analysis/trivy.json",
                Detail = "Scanner timed out after 1200 s."
            },
            new AnalysisGateErrorCauseDto { Kind = AnalysisGateErrorCauseKind.DependencyTrack, PipelineRunId = 3100, ScannerKey = "dependency-track", Detail = "pending" }
        ]
    };

    [Fact]
    public void AnErrorGate_NamesEachCause_WithTheProducerTheStepTheReportAndTheReason()
    {
        var lines = PipelineRunGateErrorCauses.Lines(ErrorGateWithAGoodLetter(), Localizer);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Trivy (trivy-dependencies), étape « Scan dependencies », rapport #4, artefact #7, fichier .analysis/trivy.json, run 3100"
            + " : l'outil a dépassé son délai. Détail : Scanner timed out after 1200 s.", lines[0]);
        Assert.Equal("dependency-track, run 3100 : envoi du SBOM non terminé (état : pending).", lines[1]);
    }

    [Theory]
    [InlineData(AnalysisGateStatus.Passed)]
    [InlineData(AnalysisGateStatus.Warning)]
    [InlineData(AnalysisGateStatus.Blocked)]
    public void AGateThatIsNotInError_ListsNoCause(AnalysisGateStatus status) =>
        Assert.Empty(PipelineRunGateErrorCauses.Lines(ErrorGateWithAGoodLetter() with { Status = status }, Localizer));

    [Fact]
    public void AnErrorGate_ShowsNoLetter_WhateverTheAnalysisOrTheSealSay()
    {
        var gate = ErrorGateWithAGoodLetter();

        Assert.Null(PipelineRunGatePresentation.HeadlineGrade(gate, null));
        Assert.Null(PipelineRunGatePresentation.HeadlineGrade(gate, AnalysisGrade.B));
        Assert.False(PipelineRunGatePresentation.GradeComesFromSeal(gate, AnalysisGrade.B));
        // The letter comes back with any other verdict.
        Assert.Equal(AnalysisGrade.A, PipelineRunGatePresentation.HeadlineGrade(gate with { Status = AnalysisGateStatus.Passed }, null));
    }

    [Fact]
    public void TheGateTab_OfAnErrorGate_ListsTheCauses_AndDrawsNoGradeA()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings", new AnalysisRunFindingsPageDto { Page = 1, PageSize = 25 });
        var gate = ErrorGateWithAGoodLetter();

        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));

        Assert.Equal(2, cut.FindAll("[data-testid='gate-error-cause']").Count);
        var gateRow = cut.Find("[data-testid='grade-row-gate']");
        Assert.DoesNotContain("grade-badge--a", gateRow.InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGateTab_OfAGateThatPassed_DrawsNoErrorAlert()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings", new AnalysisRunFindingsPageDto { Page = 1, PageSize = 25 });
        var gate = ErrorGateWithAGoodLetter() with { Status = AnalysisGateStatus.Passed, ErrorCauses = [] };

        var cut = Render<PipelineRunGateFindings>(parameters => parameters.Add(component => component.Gate, gate));

        Assert.Empty(cut.FindAll("[data-testid='gate-error-causes']"));
        Assert.Contains("grade-badge--a", cut.Find("[data-testid='grade-row-gate']").InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMergedGateOfARunTree_KeepsTheCausesOfEveryChild()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/3097/result", new AnalysisRunGateDto
        {
            PipelineRunId = 3097,
            Status = AnalysisGateStatus.Passed,
            Grade = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.A }
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/3100/result", ErrorGateWithAGoodLetter());
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings", new AnalysisRunFindingsPageDto { Page = 1, PageSize = 1 });
        var state = new PipelineRunGateState();

        await state.LoadAsync(
            Services.GetRequiredService<ApiClient>(),
            new PipelineRunDto { Id = 3097, Status = PipelineStatus.Failed },
            [new PipelineRunDto { Id = 3100, Status = PipelineStatus.Failed }],
            ct: Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisGateStatus.Error, state.Result!.Status);
        Assert.Equal(2, state.Result.ErrorCauses.Count);
        Assert.All(state.Result.ErrorCauses, cause => Assert.Equal(3100, cause.PipelineRunId));
    }

    private sealed class FormatLocalizer(IReadOnlyDictionary<string, string> values) : IStringLocalizer<Aetheus.Front.Resources.AppStrings>
    {
        public LocalizedString this[string name] => new(name, values.TryGetValue(name, out var value) ? value : name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(this[name].Value, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
