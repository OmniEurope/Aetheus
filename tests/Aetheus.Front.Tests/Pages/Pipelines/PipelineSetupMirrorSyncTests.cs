// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-23 (user decision 2026-11-09): for a project whose source is an external repository, the wizard
/// synchronizes the mirror before it reads the repository, instead of reading an empty mirror and ending on "production not
/// prepared"; a failed synchronization is said, with a retry. R-14: the Capabilities step takes the whole width. The API is a
/// double of the HTTP handler: that the real sync endpoint clones the repository is not proved here.</summary>
public sealed class PipelineSetupMirrorSyncTests : BunitContext
{
    private const string Sync = "api/external-repos/project/7/sync";
    private const string Readiness = "api/pipelines/setup/readiness";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupMirrorSyncTests() => _handler = BunitTestHelper.RegisterServices(this);

    private void Arrange(ExternalRepoDto? attached, ExternalRepoDto? synchronized)
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", new[] { "application-ci", "application-quality", "application-qa" }
            .Select((name, index) => new PipelineTemplateSummaryDto { Id = index + 1, Name = name, Version = 1 }).ToList());
        _handler.SetJsonResponse(HttpMethod.Post, Readiness, new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" });
        if (attached is not null) _handler.SetJsonResponse("api/external-repos/project/7", attached);
        if (synchronized is not null) _handler.SetJsonResponse(HttpMethod.Post, Sync, synchronized);
    }

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenCapabilitiesAsync()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='setup-option-ci']")));
        return cut;
    }

    private static ExternalRepoDto Mirror(GitMirrorStatus status, string? error = null) =>
        new() { ProjectId = 7, MirrorStatus = status, LastFetchError = error };

    private List<(string Method, string Url)> Requests() => _handler.RequestsSnapshot();

    [Fact]
    public async Task AnExternalRepository_IsSynchronizedBeforeTheRepositoryIsRead()
    {
        Arrange(Mirror(GitMirrorStatus.NotInitialized), Mirror(GitMirrorStatus.Ready));

        var cut = await OpenCapabilitiesAsync();

        cut.WaitForAssertion(() =>
        {
            var requests = Requests();
            var synced = requests.FindIndex(request => request.Method == "POST" && request.Url.EndsWith(Sync, StringComparison.Ordinal));
            var read = requests.FindIndex(request => request.Method == "POST" && request.Url.EndsWith(Readiness, StringComparison.Ordinal));
            Assert.True(synced >= 0, "the mirror is synchronized");
            Assert.True(read > synced, "the repository is read after the mirror was synchronized");
        });
        Assert.Empty(cut.FindAll("[data-testid='setup-mirror-sync-failed']"));
    }

    [Fact]
    public async Task AProjectWithoutAnExternalRepository_SynchronizesNothing()
    {
        Arrange(attached: null, synchronized: null);

        await OpenCapabilitiesAsync();

        Assert.DoesNotContain(Requests(), request => request.Url.Contains("/sync", StringComparison.Ordinal));
    }

    /// <summary>R-27 (project oe1): the project names an external address and has no mirror: the wizard creates the mirror through
    /// the project endpoint before it reads the repository, and says the server's refusal when it fails.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnExternalAddressWithoutMirror_GetsItsMirrorCreatedBeforeTheRepositoryIsRead(bool accepted)
    {
        const string create = "api/external-repos/project/7/mirror-address";
        Arrange(attached: null, synchronized: null);
        _handler.SetPaginatedJsonResponse("api/projects",
            [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop", RepositoryUrl = "https://github.com/team/app.git" }]);
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>());
        if (accepted) _handler.SetJsonResponse(HttpMethod.Post, create, Mirror(GitMirrorStatus.Ready));
        else
            _handler.SetRawResponse(HttpMethod.Post, create, """{"message":"Could not reach the external repository: Remote unreachable."}""",
                "application/json", System.Net.HttpStatusCode.BadRequest);

        var cut = await OpenCapabilitiesAsync();

        cut.WaitForAssertion(() =>
        {
            var requests = Requests();
            var created = requests.FindIndex(request => request.Method == "POST" && request.Url.EndsWith(create, StringComparison.Ordinal));
            Assert.True(created >= 0, "the mirror is created");
            if (accepted)
                Assert.True(requests.FindIndex(request => request.Url.EndsWith(Readiness, StringComparison.Ordinal)) > created,
                    "the repository is read after the mirror was created");
        });
        if (accepted) Assert.Empty(cut.FindAll("[data-testid='setup-mirror-sync-failed']"));
        else
            cut.WaitForAssertion(() => Assert.Contains("PipelineSetupMirrorCreateFailed",
                cut.Find("[data-testid='setup-mirror-sync-failed']").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMirrorThatFailsToSynchronize_IsSaidHonestly_AndCanBeRetried()
    {
        Arrange(Mirror(GitMirrorStatus.NotInitialized), Mirror(GitMirrorStatus.Error, "authentication refused"));

        var cut = await OpenCapabilitiesAsync();

        cut.WaitForAssertion(() => Assert.Contains("PipelineSetupMirrorSyncFailed",
            cut.Find("[data-testid='setup-mirror-sync-failed']").TextContent, StringComparison.Ordinal));
        var attempts = Requests().Count(request => request.Method == "POST" && request.Url.EndsWith(Sync, StringComparison.Ordinal));

        _handler.SetJsonResponse(HttpMethod.Post, Sync, Mirror(GitMirrorStatus.Ready));
        await cut.Find("[data-testid='setup-mirror-sync-retry']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='setup-mirror-sync-failed']")));
        Assert.Equal(attempts + 1, Requests().Count(request => request.Method == "POST" && request.Url.EndsWith(Sync, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheCapabilitiesStep_TakesTheWholeWidth_WithTilesThatWrap()
    {
        Arrange(attached: null, synchronized: null);

        var cut = await OpenCapabilitiesAsync();

        var step = cut.Find("[data-testid='setup-option-ci']").Closest(".pipeline-setup-step");
        Assert.NotNull(step);
        Assert.Contains("pipeline-setup-step--full", step!.ClassName, StringComparison.Ordinal);
    }
}
