// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Analysis;

public sealed class AnalysisFindingDetailTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AnalysisFindingDetailTests() => _handler = BunitTestHelper.RegisterServices(this);

    [Fact]
    public void R504_OpenedFromARun_TheFileLinkIsThatRunsCommit_NotTheLatestOccurrence()
    {
        Services.GetRequiredService<PermissionService>().SetPermissions([], isAdmin: true);
        var latest = new AnalysisFindingOccurrenceDto
        {
            Id = 20,
            PipelineRunId = 2480,
            RepositoryId = 42,
            FilePath = "src/Moved.cs",
            StartLine = 9,
            CommitHash = "latestcommit000",
            BranchName = "develop"
        };
        var ofTheRun = new AnalysisFindingOccurrenceDto
        {
            Id = 11,
            PipelineRunId = 2472,
            RepositoryId = 42,
            FilePath = "src/Sample.cs",
            StartLine = 42,
            CommitHash = "runcommit2472",
            BranchName = "develop"
        };
        _handler.SetJsonResponse("api/analysis/findings/7", new AnalysisFindingDto
        {
            Id = 7,
            ProjectId = 1,
            Title = "Command injection",
            Status = AnalysisFindingStatus.Open,
            LatestOccurrence = latest
        });
        _handler.SetJsonResponse("api/analysis/findings/7/occurrences?take=100", new List<AnalysisFindingOccurrenceDto> { latest, ofTheRun });
        _handler.SetJsonResponse("api/analysis/findings/7/decisions", new List<AnalysisFindingDecisionDto>());
        _handler.SetJsonResponse("api/git/repos/42/blob", new GitLightBlobDto { Path = "src/Sample.cs", Content = "line" });
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("/analysis/findings/7?run=2472");

        var cut = Render<AnalysisFindingDetail>(parameters => parameters.Add(component => component.Id, 7));

        cut.WaitForAssertion(() => Assert.Equal("src/Sample.cs", cut.Find(".analysis-source-location code").TextContent), TimeSpan.FromSeconds(3));
        cut.Find(".analysis-source-location button.analysis-source-open").Click();
        Assert.Contains("ref=runcommit2472", navigation.Uri);
        Assert.Contains("path=src%2FSample.cs", navigation.Uri);
    }

    [Fact]
    public void RendersBoundedSourceAndPipelineRunLink()
    {
        Services.GetRequiredService<PermissionService>().SetPermissions([], isAdmin: true);
        var occurrence = new AnalysisFindingOccurrenceDto
        {
            Id = 9,
            AnalysisReportId = 12,
            PipelineRunId = 88,
            RepositoryId = 42,
            ToolName = "OpenGrep",
            ScannerKey = "opengrep",
            RuleId = "aetheus.test.rule",
            FilePath = "src/Sample.cs",
            StartLine = 42,
            EndLine = 46,
            Symbol = "Sample.Execute",
            Message = "Untrusted input reaches a command sink.",
            BranchName = "feature/security",
            CommitHash = "0123456789abcdef",
            IsNew = true,
            CreatedAt = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc)
        };
        _handler.SetJsonResponse("api/analysis/findings/7", new AnalysisFindingDto
        {
            Id = 7,
            ProjectId = 1,
            RuleId = occurrence.RuleId,
            Title = "Command injection",
            Message = "User-controlled data can execute an operating-system command.",
            Category = AnalysisCategory.Sast,
            Severity = AnalysisSeverity.High,
            Status = AnalysisFindingStatus.Open,
            LatestOccurrence = occurrence
        });
        _handler.SetJsonResponse("api/analysis/findings/7/occurrences?take=100", new List<AnalysisFindingOccurrenceDto> { occurrence });
        _handler.SetJsonResponse("api/analysis/findings/7/decisions", new List<AnalysisFindingDecisionDto>());
        _handler.SetJsonResponse("api/git/repos/42/blob", new GitLightBlobDto
        {
            Path = "src/Sample.cs",
            Content = string.Join('\n', Enumerable.Range(1, 60).Select(line => $"source line {line}"))
        });

        var cut = Render<AnalysisFindingDetail>(parameters => parameters.Add(component => component.Id, 7));

        cut.WaitForState(() => cut.Markup.Contains("src/Sample.cs", StringComparison.Ordinal), TimeSpan.FromSeconds(3));
        Assert.Contains("Sample.Execute", cut.Markup);
        Assert.Contains("Untrusted input reaches a command sink.", cut.Markup);
        Assert.Contains("pipelines/runs/88", cut.Markup);
        Assert.Equal("src/Sample.cs", cut.Find(".analysis-source-location code").TextContent);
        // Recette R-433: the line is a badge, "Open the file" a standard button, the passage under them.
        Assert.Contains("Line 42-46", cut.Find(".analysis-source-location .omni-badge").TextContent);
        cut.Find(".analysis-source-location button.analysis-source-open").Click();
        var fileHref = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri;
        Assert.Contains("/git-repositories/42?tab=files", fileHref);
        Assert.Contains("ref=0123456789abcdef", fileHref);
        Assert.Contains("path=src%2FSample.cs", fileHref);
        Assert.Contains("line=42", fileHref);
        Assert.Contains(_handler.Requests, request => request.Url.Contains("api/git/repos/42/blob?ref=0123456789abcdef&path=src%2FSample.cs", StringComparison.Ordinal));
        var passage = cut.Find(".analysis-source-card .omni-code-viewer").TextContent;
        Assert.Contains("source line 39", passage);
        Assert.Contains("source line 49", passage);
        Assert.DoesNotContain("source line 38", passage);
        Assert.DoesNotContain("source line 50", passage);
        // The passage keeps the file's line numbers (OE FirstLineNumber) and marks the finding's own lines.
        var numbers = cut.FindAll(".analysis-source-card .omni-code-viewer__number").Select(number => number.TextContent).ToList();
        Assert.Equal("39", numbers[0]);
        Assert.Equal("49", numbers[^1]);
        Assert.Equal(["42", "43", "44", "45", "46"], cut.FindAll(".analysis-source-card .omni-code-viewer__line--highlighted .omni-code-viewer__number")
            .Select(number => number.TextContent));
        Assert.Contains("OpenGrep", cut.Find(".analysis-source-metadata").TextContent);
        Assert.Contains(cut.FindAll(".analysis-source-metadata a"), link =>
            link.TextContent.Contains("feature/security", StringComparison.Ordinal)
            && link.GetAttribute("href") == "/git-repositories/42?tab=files&ref=feature%2Fsecurity");
        Assert.Contains(cut.FindAll(".analysis-source-metadata a"), link =>
            link.GetAttribute("href") == "/git-repositories/42/commits/0123456789abcdef");
        Assert.Contains("User-controlled data can execute an operating-system command.", cut.Find(".analysis-finding-problem").TextContent);
        Assert.Contains("Untrusted input reaches a command sink.", cut.Find(".analysis-source-excerpt").TextContent);
        Assert.Equal(4, cut.FindAll(".analysis-finding-tabs [role='tab']").Count);
        Assert.DoesNotContain("analysis-finding-prompt-source", cut.Markup);
        Assert.DoesNotContain("analysis-decision-card", cut.Markup);
    }

    [Fact]
    public async Task PromptActions_CopyDownloadAndPreviewMarkdown()
    {
        _handler.SetJsonResponse("api/analysis/findings/8", new AnalysisFindingDto
        {
            Id = 8,
            ProjectId = 1,
            RuleId = "security.secret",
            Title = "Secret exposed",
            Message = "A credential is committed.",
            Category = AnalysisCategory.Secrets,
            Severity = AnalysisSeverity.Critical,
            Status = AnalysisFindingStatus.Open
        });
        _handler.SetJsonResponse("api/analysis/findings/8/occurrences?take=100", new List<AnalysisFindingOccurrenceDto>());
        _handler.SetJsonResponse("api/analysis/findings/8/decisions", new List<AnalysisFindingDecisionDto>());
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/analysis/findings/8?tab=prompt");
        var cut = Render<AnalysisFindingDetail>(parameters => parameters.Add(component => component.Id, 8));
        cut.WaitForState(() => cut.Markup.Contains("AnalysisAiPrompt", StringComparison.Ordinal), TimeSpan.FromSeconds(3));

        var buttons = cut.FindAll("button");
        await buttons.Single(button => button.TextContent.Contains("CopyAiPrompt", StringComparison.Ordinal)).ClickAsync(new());
        await buttons.Single(button => button.Names().Contains("DownloadMarkdown", StringComparison.Ordinal)).ClickAsync(new());
        await buttons.Single(button => button.TextContent.Contains("AnalysisPromptPreview", StringComparison.Ordinal)).ClickAsync(new());

        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "navigator.clipboard.writeText");
        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "downloadFile");
        Assert.Contains("analysis-finding-prompt-preview", cut.Markup);
    }

    [Fact]
    public void DecisionTab_RendersDecisionFormAndHistory()
    {
        Services.GetRequiredService<PermissionService>().SetPermissions([], isAdmin: true);
        _handler.SetJsonResponse("api/analysis/findings/9", new AnalysisFindingDto
        {
            Id = 9,
            ProjectId = 1,
            RuleId = "security.decision",
            Title = "Review required",
            Message = "This finding needs an explicit decision.",
            Category = AnalysisCategory.Sast,
            Severity = AnalysisSeverity.Medium,
            Status = AnalysisFindingStatus.Open
        });
        _handler.SetJsonResponse("api/analysis/findings/9/occurrences?take=100", new List<AnalysisFindingOccurrenceDto>());
        _handler.SetJsonResponse("api/analysis/findings/9/decisions", new List<AnalysisFindingDecisionDto>());
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/analysis/findings/9?tab=decisions");

        var cut = Render<AnalysisFindingDetail>(parameters => parameters.Add(component => component.Id, 9));

        cut.WaitForState(() => cut.Markup.Contains("analysis-decision-card", StringComparison.Ordinal), TimeSpan.FromSeconds(3));
        // Recettes R2-130, R2-131: no arbitration instruction, no "required"/"optional" hints.
        Assert.DoesNotContain("AnalysisDecisionHelp", cut.Find(".analysis-decision-card").TextContent);
        Assert.Equal(3, cut.FindAll(".analysis-decision-choice").Count);
        Assert.Equal(2, cut.FindAll(".analysis-decision-details .omni-form-field").Count);
        Assert.Empty(cut.FindAll(".analysis-decision-outcome .omni-drop-down"));
        Assert.DoesNotContain("AnalysisDecisionReasonHint", cut.Find(".analysis-decision-card").TextContent);
        Assert.DoesNotContain("AnalysisDecisionExpirationHint", cut.Find(".analysis-decision-card").TextContent);
        // Recette R2-134: an open finding reads as open; nothing is chosen until the reviewer picks.
        Assert.Contains("AnalysisFindingStatusOpen", cut.Find(".analysis-decision-selected").TextContent);
        Assert.Empty(cut.FindAll(".analysis-decision-choice.is-selected"));
        Assert.Contains("AnalysisDecisionHistory", cut.Markup);
    }

    [Fact]
    public async Task DecisionTab_SubmitsAnExplicitAuditableDecision()
    {
        Services.GetRequiredService<PermissionService>().SetPermissions([], isAdmin: true);
        _handler.SetJsonResponse("api/analysis/findings/10", new AnalysisFindingDto
        {
            Id = 10,
            ProjectId = 1,
            RuleId = "security.review",
            Title = "Review required",
            Message = "This finding needs an explicit decision.",
            Category = AnalysisCategory.Sast,
            Severity = AnalysisSeverity.Medium,
            Status = AnalysisFindingStatus.Open
        });
        _handler.SetJsonResponse("api/analysis/findings/10/occurrences?take=100", new List<AnalysisFindingOccurrenceDto>());
        _handler.SetJsonResponse("api/analysis/findings/10/decisions", new List<AnalysisFindingDecisionDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/analysis/findings/10/decisions", new AnalysisFindingDecisionDto
        {
            Id = 3,
            AnalysisFindingId = 10,
            Status = AnalysisFindingStatus.Accepted,
            Reason = "Risk accepted after architecture review.",
            CreatedByUsername = "admin"
        });
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/analysis/findings/10?tab=decisions");
        var cut = Render<AnalysisFindingDetail>(parameters => parameters.Add(component => component.Id, 10));
        cut.WaitForState(() => cut.Markup.Contains("analysis-decision-card", StringComparison.Ordinal), TimeSpan.FromSeconds(3));

        cut.Find("textarea").Input("Risk accepted after architecture review.");
        // Recette R2-134: no outcome is preselected, so a submit without a choice sends nothing.
        BunitTestHelper.Interactive(this);
        cut.Find(".analysis-decision-form form").Submit();
        Assert.DoesNotContain(_handler.Requests, request => request.Method == "POST");

        cut.Find(".analysis-decision-choice .omni-button").Click();
        cut.Find(".analysis-decision-form form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.Method == "POST" && request.Url.Contains("api/analysis/findings/10/decisions", StringComparison.Ordinal)));
    }
}
