// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Shared.Components.Git;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-94 (user decision 2026-10-25): the wizard proposes the source branch, develop when the repository has
/// it and the project's default branch otherwise, and writes it into every pipeline it creates as <c>source_branch:</c>,
/// like <c>aetheus-deploy-prod</c>. Without it the pipelines read the project's default branch (main for W6).</summary>
public sealed class PipelineSetupSourceBranchTests : BunitContext
{
    private const string BranchField = "oe-formfield-pages-pipelines-pipelinesetupwizardstep-branch";

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupSourceBranchTests() => _handler = BunitTestHelper.RegisterServices(this);

    private void Arrange(string defaultBranch, params string[] branches)
    {
        _handler.SetPaginatedJsonResponse("api/projects",
            [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = defaultBranch, InternalRepositoryId = 5 }]);
        _handler.SetPaginatedJsonResponse("api/git/repos/5/branches",
            branches.Select(name => new GitLightBranchDto { Name = name, IsDefault = name == defaultBranch }));
        _handler.SetJsonResponse("api/git/repos/5", new GitLightRepoDto { Id = 5, DefaultBranch = defaultBranch });
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = defaultBranch });
    }

    private IRenderedComponent<PipelineSetupWizard> Open(string query = "projectId=7")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"http://localhost/pipelines/setup?{query}");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static async Task ToSummaryAsync(IRenderedComponent<PipelineSetupWizard> cut)
    {
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
    }

    private async Task<CreatePipelineRequest[]> FinishAsync(IRenderedComponent<PipelineSetupWizard> cut)
    {
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(7, Created().Length));
        return Created();
    }

    private CreatePipelineRequest[] Created() => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
        .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
        .ToArray();

    private static void AssertEveryPipelineReads(string branch, CreatePipelineRequest[] created) =>
        Assert.All(created, request =>
        {
            Assert.Equal(branch, request.SourceBranch);
            Assert.Equal(branch, Yaml.Deserialize<Dictionary<object, object>>(request.YamlDefinition)["source_branch"]);
        });

    [Fact]
    public async Task WhenTheRepositoryHasDevelop_EveryPipelineIsCreatedOnIt_EvenWhenTheDefaultBranchIsMain()
    {
        Arrange("main", "main", "develop");
        var cut = Open();
        await ToSummaryAsync(cut);

        var created = await FinishAsync(cut);

        AssertEveryPipelineReads("develop", created);
    }

    [Fact]
    public async Task WhenTheRepositoryHasNoDevelop_EveryPipelineIsCreatedOnTheDefaultBranch()
    {
        Arrange("main", "main", "release");
        var cut = Open();
        await ToSummaryAsync(cut);

        var created = await FinishAsync(cut);

        AssertEveryPipelineReads("main", created);
    }

    [Fact]
    public async Task WhenTheBranchesAnswerIsNotJson_EveryPipelineIsCreatedOnTheDefaultBranch()
    {
        Arrange("main", "main", "develop");
        _handler.SetRawResponse("api/git/repos/5/branches", "<html>proxy error</html>");
        var cut = Open();
        await ToSummaryAsync(cut);

        var created = await FinishAsync(cut);

        AssertEveryPipelineReads("main", created);
    }

    [Fact]
    public async Task WhenTheBranchesReadTimesOut_EveryPipelineIsCreatedOnTheDefaultBranch()
    {
        Arrange("main", "main", "develop");
        _handler.SetAsyncJsonResponse<PaginatedResult<GitLightBranchDto>>(HttpMethod.Get, "api/git/repos/5/branches",
            _ => Task.FromException<PaginatedResult<GitLightBranchDto>>(new TaskCanceledException("Test: timeout")));
        var cut = Open();
        await ToSummaryAsync(cut);

        var created = await FinishAsync(cut);

        AssertEveryPipelineReads("main", created);
    }

    /// <summary>R-76 (W7, 2026-10-27): a project created without a default branch has none to fall back on, so a repository
    /// with no develop produced pipelines with no <c>source_branch:</c> at all. The repository's own default branch is
    /// still a value to write.</summary>
    [Fact]
    public async Task WhenTheProjectHasNoDefaultBranchAndTheRepositoryNoDevelop_EveryPipelineIsCreatedOnTheRepositoryDefaultBranch()
    {
        Arrange("main", "main", "release");
        _handler.SetPaginatedJsonResponse("api/projects",
            [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = null, InternalRepositoryId = 5 }]);
        _handler.SetJsonResponse("api/git/repos/5", new GitLightRepoDto { Id = 5, DefaultBranch = "main" });
        var cut = Open();
        await ToSummaryAsync(cut);

        var created = await FinishAsync(cut);

        AssertEveryPipelineReads("main", created);
    }

    [Fact]
    public async Task WhenTheRepositoryHasNoBranchYet_EveryPipelineIsCreatedOnTheRepositoryDefaultBranch()
    {
        Arrange("main");
        _handler.SetPaginatedJsonResponse("api/projects",
            [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = null, InternalRepositoryId = 5 }]);
        _handler.SetJsonResponse("api/git/repos/5", new GitLightRepoDto { Id = 5, DefaultBranch = "main" });
        var cut = Open();
        await ToSummaryAsync(cut);

        var created = await FinishAsync(cut);

        AssertEveryPipelineReads("main", created);
    }

    [Fact]
    public async Task TheProjectDefaultBranch_WinsOverTheRepositoryOne_WhenThereIsNoDevelop()
    {
        Arrange("trunk", "trunk", "release");
        _handler.SetJsonResponse("api/git/repos/5", new GitLightRepoDto { Id = 5, DefaultBranch = "main" });
        var cut = Open();
        await ToSummaryAsync(cut);

        var created = await FinishAsync(cut);

        AssertEveryPipelineReads("trunk", created);
    }

    /// <summary>R-76 (b): a project with no default branch and no internal repository names no branch at all; the pipelines
    /// would be written without <c>source_branch:</c>, which the branch step now says instead of staying silent.</summary>
    [Fact]
    public void WhenNeitherTheProjectNorAnyRepositoryNamesABranch_TheStepWarnsInsteadOfStayingSilent()
    {
        Arrange("main");
        _handler.SetPaginatedJsonResponse("api/projects",
            [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = null, InternalRepositoryId = null }]);

        var cut = Open();

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[data-testid='setup-source-branch-missing']");
            Assert.Contains("PipelineSourceBranchMissing", alert.TextContent, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(cut.FindComponents<OmniDropDown<string>>(), item => item.Instance.Id == BranchField);
    }

    [Theory]
    [InlineData("main")]
    [InlineData(null)]
    public void WhenABranchIsKnown_TheStepShowsTheBranchAndNoWarning(string? projectDefault)
    {
        Arrange("main", "main");
        _handler.SetPaginatedJsonResponse("api/projects",
            [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = projectDefault, InternalRepositoryId = 5 }]);
        _handler.SetJsonResponse("api/git/repos/5", new GitLightRepoDto { Id = 5, DefaultBranch = "main" });

        var cut = Open();

        cut.WaitForAssertion(() => Assert.Equal("main", BranchDropdown(cut).Instance.Value));
        Assert.Empty(cut.FindAll("[data-testid='setup-source-branch-missing']"));
    }

    [Fact]
    public void WhileTheBranchesAreStillBeingRead_NoWarningFlashes()
    {
        Arrange("main", "main");
        _handler.SetPaginatedJsonResponse("api/projects",
            [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = null, InternalRepositoryId = 5 }]);
        var release = new TaskCompletionSource();
        _handler.SetAsyncJsonResponse<GitLightRepoDto>(HttpMethod.Get, "api/git/repos/5", async _ =>
        {
            await release.Task;
            return new GitLightRepoDto { Id = 5, DefaultBranch = "main" };
        });

        var cut = Open();

        Assert.Empty(cut.FindAll("[data-testid='setup-source-branch-missing']"));
        release.SetResult();
        cut.WaitForAssertion(() => Assert.Equal("main", BranchDropdown(cut).Instance.Value));
        Assert.Empty(cut.FindAll("[data-testid='setup-source-branch-missing']"));
    }

    private static IRenderedComponent<OmniDropDown<string>> BranchDropdown(IRenderedComponent<PipelineSetupWizard> cut) =>
        cut.FindComponents<OmniDropDown<string>>().Single(item => item.Instance.Id == BranchField);

    private static async Task PickAsync(IRenderedComponent<PipelineSetupWizard> cut, string branch)
    {
        cut.WaitForAssertion(() => Assert.NotNull(BranchDropdown(cut)));
        await cut.InvokeAsync(() => BranchDropdown(cut).Instance.ValueChanged.InvokeAsync(branch));
    }

    [Fact]
    public async Task TheBranchTheUserPicks_IsTheOneWritten_AndTheDropdownOffersTheRepositoryBranches()
    {
        Arrange("main", "main", "develop", "release");
        var cut = Open();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("develop", BranchDropdown(cut).Instance.Value);
            Assert.Equal(["develop", "main", "release"],
                BranchDropdown(cut).Instance.Options.Select(option => option.Value).Order(StringComparer.Ordinal));
        });
        await PickAsync(cut, "release");
        await ToSummaryAsync(cut);

        var created = await FinishAsync(cut);

        AssertEveryPipelineReads("release", created);
    }

    [Fact]
    public async Task TheBranchTheUserPicks_TravelsInTheSummaryAddress()
    {
        Arrange("main", "main", "develop", "release");
        var cut = Open();
        await PickAsync(cut, "release");

        await ToSummaryAsync(cut);

        Assert.Contains("&branch=release", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASummaryAddressThatNamesABranch_IsTakenBackAsItIs()
    {
        Arrange("main", "main", "develop", "release");
        var cut = Open("projectId=7&prefix=atlas&options=ci%2Cquality%2Csecurity%2Cqa%2Ccandidate%2Cnightly%2Cproduction%2Cbackend"
            + "&architecture=application&sameProject=no&branch=release&step=summary");
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));

        var created = await FinishAsync(cut);

        AssertEveryPipelineReads("release", created);
    }

    [Fact]
    public void TheSourceBranch_IsWrittenAtTheRootOfEveryYaml_AndOnlyWhenKnown()
    {
        var input = new PipelineSetupYamlInput(
            "atlas", (true, true, false), "package-web-analytics-dotnet",
            Rollback: true, Quality: true, Security: true, Qa: true, QaExtended: false, SecurityHistory: false,
            ["ci", "candidate", "deploy-prod"], "develop");

        foreach (var template in new[] { "application-ci", "application-candidate", "application-deploy-prod", "application-qa" })
        {
            var yaml = Yaml.Deserialize<Dictionary<object, object>>(PipelineSetupYamlBuilder.Build($"atlas-{template}", template, 1, input));
            Assert.Equal("develop", yaml["source_branch"]);
        }
        Assert.DoesNotContain("source_branch", PipelineSetupYamlBuilder.Build("atlas-ci", "application-ci", 1, input with { SourceBranch = null }), StringComparison.Ordinal);
    }

    private static List<PipelineTemplateSummaryDto> TemplateCatalog() =>
    [
        .. new[]
        {
            "application-ci", "application-quality", "application-security", "application-qa",
            "application-candidate", "application-nightly", "application-deploy-prod"
        }.Select(name => new PipelineTemplateSummaryDto { Id = name.GetHashCode(StringComparison.Ordinal), Name = name, Version = 1 })
    ];
}
