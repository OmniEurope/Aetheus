// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Shared;
using Aetheus.Front.Tests.TestDoubles;
using Aetheus.Shared.Components.Git;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-20 and R-6 (W8): once the wizard created the Git repository it offers an optional first push (the commands to
/// copy, main then develop). Skipped, the wizard goes on as it always did. Accepted, it waits for what is pushed, holds the
/// creation of the pipelines back, and goes on as soon as the repository holds a branch: the readiness verdict, the branches and
/// the production setup are read again on the branch pushed. The reads of the repository run on the page's clock, so the
/// tests move a fake clock instead of waiting.</summary>
public sealed class PipelineSetupFirstPushTests : BunitContext
{
    private const string Readiness = "api/pipelines/setup/readiness";
    private const string Analysis = "api/pipelines/setup/production-analysis";
    private const string Branches = "api/git/repos/8/branches";
    private const string CloneUrl = "https://aetheus.test/git/atlas.git";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ObservedFakeClock _clock = new(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
    private readonly List<GitLightBranchDto> _pushed = [];
    private bool _repositoryCreated;
    private PipelineProductionAnalysisDto _analysis = new();

    public PipelineSetupFirstPushTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<TimeProvider>(_clock);
        BunitTestHelper.UseImmediateDialogs(this);
        ((ImmediateDialogService)Services.GetRequiredService<OmniDialogService>()).OpenResult = 8;
    }

    private void Arrange()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "main" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse("api/git/repos/8", new GitLightRepoDto { Id = 8, DefaultBranch = "main", CloneUrl = CloneUrl, IsEmpty = true });
        _handler.SetAsyncJsonResponse<PaginatedResult<GitLightBranchDto>>(HttpMethod.Get, Branches, _ => Task.FromResult(
            new PaginatedResult<GitLightBranchDto>
            {
                Items = [.. _pushed],
                TotalCount = _pushed.Count,
                Page = 1,
                PageSize = Math.Max(_pushed.Count, 1)
            }));
        _handler.SetAsyncJsonResponse<PipelineSetupReadinessDto>(HttpMethod.Post, Readiness, _ => Task.FromResult(
            !_repositoryCreated
                ? new PipelineSetupReadinessDto
                {
                    Checks = [new PipelineSetupReadinessCheckDto { Kind = PipelineSetupReadinessKind.NoRepository, Severity = PipelineSetupReadinessSeverity.Blocking }]
                }
                : new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = _pushed.Count == 0 ? "main" : "develop" }));
        _handler.SetAsyncJsonResponse<PipelineProductionAnalysisDto>(HttpMethod.Post, Analysis, _ => Task.FromResult(_analysis));
    }

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenSummaryAsync()
    {
        Arrange();
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    /// <summary>The user creates the repository from the summary: the dialog hands back the id 8, the host reads again.</summary>
    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenSummaryAndCreateTheRepositoryAsync()
    {
        var cut = await OpenSummaryAsync();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-readiness-create-repository']")));
        _repositoryCreated = true;
        await cut.Find("[data-testid='setup-readiness-create-repository']").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-first-push']")));
        return cut;
    }

    private static bool FinishDisabled(IRenderedComponent<PipelineSetupWizard> cut) =>
        cut.Find(".omni-wizard__actions button.omni-wizard__finish").HasAttribute("disabled");

    private int Requests(HttpMethod method, string urlEnd) => _handler.RequestDetails.Count(request =>
        request.Method == method.Method && request.Url.Contains(urlEnd, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<string> PostBodies(string urlEnd) => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method && request.Url.EndsWith(urlEnd, StringComparison.OrdinalIgnoreCase))
        .Select(request => request.Body!);

    [Fact]
    public async Task OnceTheRepositoryIsCreated_TheCommandsToCopyArePushedMainThenDevelop_AndNothingBlocksTheCreation()
    {
        var cut = await OpenSummaryAndCreateTheRepositoryAsync();

        var commands = cut.FindComponent<OmniCodeBlock>().Instance;
        Assert.True(commands.ShowCopy);
        Assert.Equal(
            $"git remote add origin {CloneUrl}\ngit branch -M main\ngit push -u origin main\ngit push origin main:develop",
            commands.Code);
        // The step is optional: offered, not accepted, it holds nothing back.
        cut.WaitForAssertion(() => Assert.False(FinishDisabled(cut)));
        Assert.Equal(0, Requests(HttpMethod.Get, Branches));
    }

    /// <summary>R-52: besides the commands, a read-only field holds the repository address alone, with its own Copy button.</summary>
    [Fact]
    public async Task TheOffer_ShowsTheRepositoryAddressAlone_InAReadOnlyFieldWithItsOwnCopyButton()
    {
        var cut = await OpenSummaryAndCreateTheRepositoryAsync();

        var field = cut.FindComponent<OmniTextBox>().Instance;
        Assert.Equal(CloneUrl, field.Value);
        Assert.True(field.ReadOnly);
        Assert.True(field.ShowCopy);
        Assert.Equal(CloneUrl, cut.Find("[data-testid='setup-first-push-url']").GetAttribute("value"));
        // The Copy button is welded to the field itself, not the one of the commands block.
        Assert.NotEmpty(cut.FindComponent<OmniTextBox>().FindAll("button"));
    }

    [Fact]
    public async Task SkippingTheStep_KeepsTodaysBehaviour_NoWaitingAndThePipelinesAreCreated()
    {
        var cut = await OpenSummaryAndCreateTheRepositoryAsync();

        // The skip is in the menu of the one main action.
        await cut.Find("[data-testid='setup-first-push-accept'] .omni-split-button__toggle, [data-testid='setup-first-push-accept'] button[aria-haspopup]")
            .ClickAsync(new MouseEventArgs());
        await cut.Find(".setup-first-push-skip").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='setup-first-push']")));
        _clock.Advance(PipelineSetupFirstPush.PollInterval * 3);
        Assert.Equal(0, Requests(HttpMethod.Get, Branches));
        Assert.False(FinishDisabled(cut));

        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(7, PostBodies("api/pipelines").Count()));
    }

    /// <summary>R-55: while "Créer les pipelines" runs, the page veil is held and the wizard stays mounted under it; the
    /// hold goes when the creation ends.</summary>
    [Fact]
    public async Task WhileThePipelinesAreCreated_ThePageVeilIsHeld_AndTheWizardStaysMounted()
    {
        var cut = await OpenSummaryAndCreateTheRepositoryAsync();
        await cut.Find("[data-testid='setup-first-push-accept'] .omni-split-button__toggle, [data-testid='setup-first-push-accept'] button[aria-haspopup]")
            .ClickAsync(new MouseEventArgs());
        await cut.Find(".setup-first-push-skip").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.False(FinishDisabled(cut)));
        var release = new TaskCompletionSource<PipelineDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Post, "api/pipelines", _ => release.Task);
        var veil = Services.GetRequiredService<BusyVeil>();
        Assert.False(veil.IsBusy);

        var finish = cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());

        try
        {
            cut.WaitForAssertion(() => Assert.True(veil.IsBusy));
            Assert.NotEmpty(cut.FindAll("[data-testid='setup-summary']"));
        }
        finally
        {
            release.SetResult(new PipelineDto { Id = 91 });
        }
        await finish;
        cut.WaitForAssertion(() => Assert.False(veil.IsBusy));
    }

    [Fact]
    public async Task Accepting_HoldsTheCreationBack_AndTheWizardGoesOnWhenThePushArrives()
    {
        _analysis = new PipelineProductionAnalysisDto { Readable = true, Branch = "main" };
        var cut = await OpenSummaryAndCreateTheRepositoryAsync();
        var readinessBefore = Requests(HttpMethod.Post, Readiness);

        await cut.Find("[data-testid='setup-first-push-accept'] button").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='setup-first-push-stop']")));

        // Waiting: nothing is created before the code is there (R-6).
        Assert.True(FinishDisabled(cut));
        await _clock.WhenArmedAsync(PipelineSetupFirstPush.PollInterval, 1);
        _clock.Advance(PipelineSetupFirstPush.PollInterval);
        await _clock.WhenArmedAsync(PipelineSetupFirstPush.PollInterval, 2);
        cut.WaitForAssertion(() => Assert.Equal(1, Requests(HttpMethod.Get, Branches)));
        Assert.True(FinishDisabled(cut));
        Assert.Empty(cut.FindAll("[data-testid='setup-first-push-received']"));

        // The push arrives: main and develop.
        _pushed.AddRange([new GitLightBranchDto { Name = "main", IsDefault = true }, new GitLightBranchDto { Name = "develop" }]);
        _analysis = new PipelineProductionAnalysisDto
        {
            Readable = true,
            Branch = "develop",
            ComposeFiles = ["deploy/compose/remote.compose.yml"],
            ComposeFile = "deploy/compose/remote.compose.yml",
            Servers = [new ProductionServerDto { Id = 1, Name = "vps" }],
            DefaultServer = "vps",
            DeclaresBackPort = true,
            Variables = [new() { Name = "PORT_FRONT", Kind = ProductionVariableKind.Port }, new() { Name = "PORT_BACK", Kind = ProductionVariableKind.Port }]
        };
        _clock.Advance(PipelineSetupFirstPush.PollInterval);

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-testid='setup-first-push-received']"));
            Assert.False(FinishDisabled(cut));
        });
        // The readiness verdict and the production setup were read again, the latter on the branch just pushed.
        Assert.True(Requests(HttpMethod.Post, Readiness) > readinessBefore);
        Assert.Equal("develop", JsonSerializer.Deserialize<PipelineProductionAnalysisRequest>(PostBodies(Analysis).Last(), Web)!.Branch);

        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(7, PostBodies("api/pipelines").Count()));
        Assert.All(PostBodies("api/pipelines").Select(body => JsonSerializer.Deserialize<CreatePipelineRequest>(body, Web)!),
            request => Assert.Equal("develop", request.SourceBranch));
    }

    // A user who pushes from his terminal without choosing "wait for my push" must not keep reading "the repository is
    // created and empty" until the final click: the offer still on screen follows the push on the same slow read.
    [Fact]
    public async Task APushMadeWhileTheOfferIsStillOnScreen_IsFollowedWithoutAcceptingOrFinishing()
    {
        _analysis = new PipelineProductionAnalysisDto { Readable = true, Branch = "main" };
        var cut = await OpenSummaryAndCreateTheRepositoryAsync();
        var readinessBefore = Requests(HttpMethod.Post, Readiness);
        await _clock.WhenArmedAsync(PipelineSetupFirstPush.PollInterval, 1);

        _pushed.AddRange([new GitLightBranchDto { Name = "main", IsDefault = true }, new GitLightBranchDto { Name = "develop" }]);
        _clock.Advance(PipelineSetupFirstPush.PollInterval);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-first-push-received']")));
        Assert.True(Requests(HttpMethod.Post, Readiness) > readinessBefore);
        Assert.False(FinishDisabled(cut));
    }

    [Fact]
    public async Task ARepositoryCreatedInTheWizard_AndPushedFromATerminalWithoutWaiting_IsWrittenOnDevelopTheFirstTime()
    {
        var cut = await OpenSummaryAndCreateTheRepositoryAsync();
        // The user does not wait in the wizard: the offer is skipped, he pushes main and develop from his terminal.
        await cut.Find("[data-testid='setup-first-push-accept'] .omni-split-button__toggle, [data-testid='setup-first-push-accept'] button[aria-haspopup]")
            .ClickAsync(new MouseEventArgs());
        await cut.Find(".setup-first-push-skip").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='setup-first-push']")));
        _pushed.AddRange([new GitLightBranchDto { Name = "main", IsDefault = true }, new GitLightBranchDto { Name = "develop" }]);

        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Equal(7, PostBodies("api/pipelines").Count()));
        Assert.All(PostBodies("api/pipelines").Select(body => JsonSerializer.Deserialize<CreatePipelineRequest>(body, Web)!),
            request => Assert.Equal("develop", request.SourceBranch));
    }

    [Fact]
    public async Task StoppingTheWait_ReleasesTheCreation_AndTheReadsStop()
    {
        var cut = await OpenSummaryAndCreateTheRepositoryAsync();
        await cut.Find("[data-testid='setup-first-push-accept'] button").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.True(FinishDisabled(cut)));

        await cut.Find("[data-testid='setup-first-push-stop']").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.False(FinishDisabled(cut)));
        await _clock.WhenNonePendingAsync(PipelineSetupFirstPush.PollInterval);
        Assert.Empty(cut.FindAll("[data-testid='setup-first-push']"));
    }

    [Fact]
    public async Task APushedRepositoryThatCannotBeRead_IsNotOffered_TheUserLandsOnTheRepositoryPageAsBefore()
    {
        Arrange();
        _handler.SetResponse(HttpMethod.Get, "api/git/repos/8", System.Net.HttpStatusCode.InternalServerError);
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-readiness-create-repository']")));

        await cut.Find("[data-testid='setup-readiness-create-repository']").ClickAsync(new MouseEventArgs());

        Assert.Empty(cut.FindAll("[data-testid='setup-first-push']"));
        Assert.StartsWith("http://localhost/git-repositories/8", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    private static List<PipelineTemplateSummaryDto> TemplateCatalog() =>
    [
        .. new[]
        {
            "application-ci", "application-quality", "application-security", "application-qa",
            "application-candidate", "application-nightly", "application-deploy-prod", "application-deploy-prod-front-back"
        }.Select(name => new PipelineTemplateSummaryDto { Id = name.GetHashCode(StringComparison.Ordinal), Name = name, Version = 1 })
    ];
}
