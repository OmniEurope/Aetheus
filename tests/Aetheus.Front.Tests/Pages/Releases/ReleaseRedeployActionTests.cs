// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Releases;
using Aetheus.Front.Tests.TestDoubles;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Releases;

/// <summary>
/// R-72: a release whose artifact is kept can be deployed again from its page and from the release list, through
/// the project's deployment pipeline (the one that declares <c>candidateVersion</c>), opened on the launch with the
/// version filled in. Without a kept artifact the action is shown disabled and says why. The flows run against the
/// real components and the real HTTP client code over a scripted handler; the launch dialog itself is answered by a
/// double, and its own one-click behaviour is proved on the real dialog in the last tests.
/// </summary>
public sealed class ReleaseRedeployActionTests : BunitContext
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ReleaseRedeployActionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetPaginatedJsonResponse("api/git/repos", Array.Empty<GitLightRepoDto>());
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "Demo", Status = ProjectStatus.Active }],
            TotalCount = 1
        });
    }

    private static List<PipelineRunParameterDto> DeploymentParameters() =>
    [
        new() { Name = "candidateVersion", Type = "string", Required = true },
        new() { Name = "deployServer", Type = "string", Required = true, Default = "vps-1" }
    ];

    /// <summary>The project's pipelines: a CI that takes no version, and the given deployment pipelines.</summary>
    private void WirePipelines(params int[] deploymentIds) => WirePipelines(new PipelinePreflightDto(), deploymentIds);

    private void WirePipelines(PipelinePreflightDto preflight, params int[] deploymentIds)
    {
        var pipelines = new List<PipelineDto> { new() { Id = 3, Name = "demo-ci", ProjectId = 1 } };
        _handler.SetJsonResponse("api/pipelines/3/parameters", new List<PipelineRunParameterDto>());
        foreach (var id in deploymentIds)
        {
            pipelines.Add(new PipelineDto { Id = id, Name = $"demo-deploy-{id}", ProjectId = 1 });
            _handler.SetJsonResponse($"api/pipelines/{id}/parameters", DeploymentParameters());
            _handler.SetJsonResponse($"api/pipelines/{id}/preflight", preflight);
            _handler.SetJsonResponse(HttpMethod.Post, $"api/pipelines/{id}/run", new PipelineRunDto { Id = 900 + id, PipelineId = id });
        }
        _handler.SetPaginatedJsonResponse("api/pipelines", pipelines);
    }

    private static ReleaseDto Release(
        int id = 7, string version = "c-old", ReleaseStatus status = ReleaseStatus.Superseded, bool withArtifact = true) => new()
        {
            Id = id,
            ProjectId = 1,
            ProjectName = "Demo",
            Version = version,
            BranchName = "main",
            Status = status,
            DetectedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            PublishedAt = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc),
            Artifacts = withArtifact ? [new ArtifactLinkDto { Id = 5, Name = "ApplicationPayload-artifacts", SizeBytes = 10 }] : []
        };

    private IRenderedComponent<ReleaseDetail> RenderDetail(ReleaseDto release)
    {
        _handler.SetJsonResponse($"api/releases/{release.Id}", release);
        var cut = Render<ReleaseDetail>(p => p.Add(c => c.ReleaseId, release.Id));
        cut.WaitForState(() => cut.Markup.Contains(release.Version, StringComparison.Ordinal));
        return cut;
    }

    private bool Sent(string method, string urlContains) =>
        _handler.Requests.Any(request => request.Method == method
            && request.Url.Contains(urlContains, StringComparison.Ordinal));

    private T LastBody<T>(string method, string urlContains) => JsonSerializer.Deserialize<T>(
        _handler.RequestDetails.Last(request => request.Method == method
            && request.Url.Contains(urlContains, StringComparison.Ordinal)).Body!, Web)!;

    private string CurrentUri => Services.GetRequiredService<NavigationManager>().Uri;

    // ---------- the release page ----------

    [Fact]
    public void ReleasePage_AReleaseWithAKeptArtifact_OffersAnEnabledRedeployInTheMenu()
    {
        var cut = RenderDetail(Release());

        var item = cut.FindMenuItem("Redeploy");

        Assert.NotNull(item);
        Assert.False(item.HasAttribute("disabled"));
        Assert.Equal("Redeploy", item.GetAttribute("title"));
    }

    [Fact]
    public void ReleasePage_RedeployIsNeverTheVisibleAction_TheMenuKeepsIt()
    {
        var cut = RenderDetail(Release());

        Assert.DoesNotContain(cut.FindAll("button"), button =>
            button.TextContent.Contains("Redeploy", StringComparison.Ordinal)
            && !button.ClassList.Contains("omni-menu__item"));
    }

    [Fact]
    public void ReleasePage_WithoutAKeptArtifact_RedeployIsDisabledAndSaysWhy()
    {
        var cut = RenderDetail(Release(withArtifact: false));

        var item = cut.FindMenuItem("Redeploy");

        Assert.NotNull(item);
        Assert.True(item.HasAttribute("disabled"));
        Assert.Equal("RedeployNoArtifact", item.GetAttribute("title"));
        item.Click();
        Assert.Equal(0, _dialog.OpenCount);
    }

    [Fact]
    public void ReleasePage_AnArchivedRelease_ShowsRedeployDisabled_NextToTheRerun()
    {
        var cut = RenderDetail(Release(status: ReleaseStatus.Archived, withArtifact: false) with
        {
            CommitHash = "0123456789abcdef0123456789abcdef01234567"
        });

        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Contains("Rerun", StringComparison.Ordinal));
        var item = cut.FindMenuItem("Redeploy");
        Assert.NotNull(item);
        Assert.True(item.HasAttribute("disabled"));
        Assert.Equal("RedeployNoArtifact", item.GetAttribute("title"));
    }

    [Theory]
    [InlineData(ReleaseStatus.Detected)]
    [InlineData(ReleaseStatus.Building)]
    [InlineData(ReleaseStatus.Failed)]
    public void ReleasePage_AReleaseThatNeverShippedAPayload_OffersNoRedeploy(ReleaseStatus status)
    {
        var cut = RenderDetail(Release(status: status));

        Assert.Null(cut.FindMenuItem("Redeploy"));
    }

    [Fact]
    public void ReleasePage_RedeployingAnOlderVersion_OpensTheDeploymentLaunchOnItsVersion_AndOneConfirmationRunsIt()
    {
        WirePipelines(40);
        _dialog.OpenResult = new Dictionary<string, string> { ["candidateVersion"] = "c-old", ["deployServer"] = "vps-1" };
        var cut = RenderDetail(Release());

        cut.FindMenuItem("Redeploy")!.Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/pipelines/40/run")));
        // Only the deployment pipeline was launched, never the CI that takes no version.
        Assert.False(Sent("POST", "api/pipelines/3/run"));
        Assert.Equal(typeof(RunParametersDialog), _dialog.LastComponent);
        var prefill = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(_dialog.LastParameters!["Prefill"]);
        Assert.Equal("c-old", prefill["candidateVersion"]);
        var body = LastBody<Dictionary<string, string>>("POST", "api/pipelines/40/run");
        Assert.Equal("c-old", body["candidateVersion"]);
        cut.WaitForAssertion(() => Assert.EndsWith("/pipelines/runs/940", CurrentUri, StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasePage_CancellingTheLaunch_RunsNothing()
    {
        WirePipelines(40);
        _dialog.OpenResult = null;
        var cut = RenderDetail(Release());

        cut.FindMenuItem("Redeploy")!.Click();

        cut.WaitForAssertion(() => Assert.Equal(typeof(RunParametersDialog), _dialog.LastComponent));
        Assert.False(Sent("POST", "api/pipelines/40/run"));
    }

    [Fact]
    public void ReleasePage_SeveralDeploymentPipelines_AsksWhichFirst_ThenLaunchesTheChosenOne()
    {
        WirePipelines(40, 41);
        _dialog.OpenResultFor = component => component == typeof(PipelineSelectDialog)
            ? 41
            : new Dictionary<string, string> { ["candidateVersion"] = "c-old", ["deployServer"] = "vps-1" };
        var cut = RenderDetail(Release());

        cut.FindMenuItem("Redeploy")!.Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/pipelines/41/run")));
        Assert.False(Sent("POST", "api/pipelines/40/run"));
        Assert.Equal([typeof(PipelineSelectDialog), typeof(RunParametersDialog)], _dialog.OpenedComponents);
        var offered = Assert.IsAssignableFrom<IEnumerable<PipelineDto>>(_dialog.OpenedParameterSets[0]!["Pipelines"]);
        Assert.Equal([40, 41], offered.Select(pipeline => pipeline.Id));
    }

    [Fact]
    public void ReleasePage_AProjectWithoutADeploymentPipeline_SaysSo_AndOpensNoLaunch()
    {
        WirePipelines();
        var cut = RenderDetail(Release());

        cut.FindMenuItem("Redeploy")!.Click();

        cut.WaitForAssertion(() => Assert.Contains(Services.Toasts(), toast =>
            toast.Severity == OmniSeverity.Warning
            && (toast.Summary + toast.Detail).Contains("NoDeployPipelineFound", StringComparison.Ordinal)));
        Assert.Equal(0, _dialog.OpenCount);
        Assert.False(Sent("POST", "/run"));
    }

    // ---------- the release list ----------

    private IRenderedComponent<ReleasesList> RenderList(params ReleaseDto[] releases)
    {
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto> { Items = [.. releases], TotalCount = releases.Length });
        var cut = Render<ReleasesList>();
        cut.WaitForState(() => cut.Markup.Contains(releases[0].Version, StringComparison.Ordinal));
        return cut;
    }

    [Fact]
    public void ReleaseList_AnOlderReleaseThatIsNotTheLastDeployment_CanBeRedeployed_ThroughTheProjectsDeploymentPipeline()
    {
        WirePipelines(40);
        _dialog.OpenResult = new Dictionary<string, string> { ["candidateVersion"] = "c-oldest", ["deployServer"] = "vps-1" };
        var cut = RenderList(Release(id: 4, version: "c-oldest"));

        var item = cut.FindMenuItem("Redeploy");
        Assert.NotNull(item);
        Assert.False(item.HasAttribute("disabled"));
        item.Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/pipelines/40/run")));
        var prefill = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(_dialog.LastParameters!["Prefill"]);
        Assert.Equal("c-oldest", prefill["candidateVersion"]);
        Assert.Equal("c-oldest", LastBody<Dictionary<string, string>>("POST", "api/pipelines/40/run")["candidateVersion"]);
    }

    [Fact]
    public void ReleaseList_WithoutAKeptArtifact_RedeployIsDisabledAndSaysWhy()
    {
        var cut = RenderList(Release(id: 4, version: "c-purged", withArtifact: false));

        var item = cut.FindMenuItem("Redeploy");

        Assert.NotNull(item);
        Assert.True(item.HasAttribute("disabled"));
        Assert.Equal("RedeployNoArtifact", item.GetAttribute("title"));
    }

    // ---------- the launch dialog itself: one confirmation ----------

    [Fact]
    public async Task LaunchDialog_OpensOnThePrefilledVersion_NotTheLatestRelease_AndOneSubmitReturnsIt()
    {
        var latest = Release(id: 9, version: "c-new", status: ReleaseStatus.Deployed) with { Deployable = true };
        var older = Release(id: 7, version: "c-old") with { Deployable = true };
        var cut = Render<RunParametersDialog>(p => p
            .Add(x => x.Parameters, DeploymentParameters())
            .Add(x => x.Prefill, new Dictionary<string, string> { ["candidateVersion"] = "c-old" })
            .Add(x => x.AvailableReleases, [latest, older]));

        Assert.Equal("c-old", cut.Find("input#candidateVersion").GetAttribute("value"));
        await cut.Find("form").SubmitAsync();

        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        cut.WaitForAssertion(() => Assert.Equal(1, dialog.CloseCount));
        var answer = Assert.IsType<Dictionary<string, string>>(dialog.LastCloseResult);
        Assert.Equal("c-old", answer["candidateVersion"]);
        Assert.Equal("vps-1", answer["deployServer"]);
    }

    [Fact]
    public async Task Redeployer_ThroughTheRealLaunchDialogFlow_PrefillsTheVersion_AndTheSubmittedAnswerStartsTheRun()
    {
        // The coordinator reads the declaration over HTTP and hands the version to the dialog as its prefill; the
        // answer a real RunParametersDialog gives for it (see above) is what the run is started with.
        WirePipelines(40);
        _dialog.OpenResult = new Dictionary<string, string> { ["candidateVersion"] = "c-old", ["deployServer"] = "vps-1" };
        var redeployer = new ReleaseRedeployer(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<PipelineRunGate>(),
            Services.GetRequiredService<PipelineRunDialogCoordinator>(),
            Services.GetRequiredService<OmniDialogService>(),
            Services.GetRequiredService<NotifyHelper>(),
            Services.GetRequiredService<NavigationManager>(),
            Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<Aetheus.Front.Resources.AppStrings>>());

        var launched = await redeployer.RedeployAsync(Release());

        Assert.True(launched);
        Assert.Equal("c-old", LastBody<Dictionary<string, string>>("POST", "api/pipelines/40/run")["candidateVersion"]);
    }

    [Fact]
    public async Task Redeployer_ARelease_WithoutAKeptArtifact_IsRefusedWithoutAnyRequest()
    {
        var redeployer = new ReleaseRedeployer(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<PipelineRunGate>(),
            Services.GetRequiredService<PipelineRunDialogCoordinator>(),
            Services.GetRequiredService<OmniDialogService>(),
            Services.GetRequiredService<NotifyHelper>(),
            Services.GetRequiredService<NavigationManager>(),
            Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<Aetheus.Front.Resources.AppStrings>>());
        var before = _handler.RequestsSnapshot().Count;

        var launched = await redeployer.RedeployAsync(Release(withArtifact: false));

        Assert.False(launched);
        Assert.Equal(before, _handler.RequestsSnapshot().Count);
        Assert.Equal(0, _dialog.OpenCount);
    }

    // ---------- R-72: deploying despite the quality gate ----------

    private static PipelinePreflightDto GateReservations() => new()
    {
        GateWarnings =
        [
            new DeploymentGateWarningDto { Kind = DeploymentGateWarningKind.NoGrade },
            new DeploymentGateWarningDto { Kind = DeploymentGateWarningKind.SecurityFindings, Count = 2 }
        ]
    };

    private object? Answer(Type component, bool confirmGate) =>
        component == typeof(DeploymentGateOverrideDialog)
            ? confirmGate
            : new Dictionary<string, string> { ["candidateVersion"] = "c-old", ["deployServer"] = "vps-1" };

    [Fact]
    public void Redeploy_TheGateHasReservations_OneConfirmationListsThem_AndConfirmingSendsTheOverride()
    {
        WirePipelines(GateReservations(), 40);
        _dialog.OpenResultFor = component => Answer(component, confirmGate: true);
        var cut = RenderDetail(Release());

        cut.FindMenuItem("Redeploy")!.Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/pipelines/40/run")));
        Assert.Equal([typeof(RunParametersDialog), typeof(DeploymentGateOverrideDialog)], _dialog.OpenedComponents);
        var listed = Assert.IsAssignableFrom<IReadOnlyList<DeploymentGateWarningDto>>(
            _dialog.OpenedParameterSets[1]![nameof(DeploymentGateOverrideDialog.Warnings)]);
        Assert.Equal([DeploymentGateWarningKind.NoGrade, DeploymentGateWarningKind.SecurityFindings], listed.Select(w => w.Kind));
        var body = LastBody<Dictionary<string, string>>("POST", "api/pipelines/40/run");
        Assert.Equal("true", body["AETHEUS_RUN_GATE_OVERRIDE"]);
        Assert.Equal("c-old", body["candidateVersion"]);
    }

    [Fact]
    public void Redeploy_GoingBackFromTheGateConfirmation_LaunchesNothing()
    {
        WirePipelines(GateReservations(), 40);
        _dialog.OpenResultFor = component => Answer(component, confirmGate: false);
        var cut = RenderDetail(Release());

        cut.FindMenuItem("Redeploy")!.Click();

        cut.WaitForAssertion(() => Assert.Equal(typeof(DeploymentGateOverrideDialog), _dialog.LastComponent), TimeSpan.FromSeconds(5));
        Assert.False(Sent("POST", "api/pipelines/40/run"));
    }

    [Fact]
    public void Redeploy_TheGateHasNoReservation_NoExtraDialog_AndNoOverrideIsSent()
    {
        WirePipelines(40);
        _dialog.OpenResultFor = component => Answer(component, confirmGate: true);
        var cut = RenderDetail(Release());

        cut.FindMenuItem("Redeploy")!.Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/pipelines/40/run")));
        Assert.Equal([typeof(RunParametersDialog)], _dialog.OpenedComponents);
        Assert.False(LastBody<Dictionary<string, string>>("POST", "api/pipelines/40/run").ContainsKey("AETHEUS_RUN_GATE_OVERRIDE"));
    }

    [Fact]
    public async Task AnOrdinaryLaunch_NeverOffersTheOverride_EvenWhenTheGateHasReservations()
    {
        _handler.SetJsonResponse("api/pipelines/40/preflight", GateReservations());

        var proceed = await Services.GetRequiredService<PipelineRunGate>().ConfirmPreflightAsync(40);

        Assert.True(proceed);
        Assert.Equal(0, _dialog.OpenCount);
    }

    [Fact]
    public void GateConfirmation_ListsEveryReasonWithItsIcon_AndItsButtonsCloseWithTheAnswer()
    {
        var cut = Render<DeploymentGateOverrideDialog>(p => p.Add(x => x.Warnings, GateReservations().GateWarnings));

        var reasons = cut.FindAll("[data-testid=gate-override-reason]");
        Assert.Equal(2, reasons.Count);
        Assert.Contains("GateWarningNoGrade", reasons[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("GateWarningSecurityFindings", reasons[1].TextContent, StringComparison.Ordinal);
        Assert.All(reasons, reason => Assert.NotNull(reason.QuerySelector("svg")));
        var confirm = cut.Find("[data-testid=gate-override-confirm]");
        Assert.Contains("GateOverrideConfirm", confirm.TextContent, StringComparison.Ordinal);
        Assert.Contains("danger", confirm.ClassName, StringComparison.OrdinalIgnoreCase);

        cut.Find("[data-testid=gate-override-back]").Click();
        Assert.Equal(false, _dialog.LastCloseResult);
        confirm.Click();
        Assert.Equal(true, _dialog.LastCloseResult);
    }

    [Fact]
    public void RunPage_ARunDeployedDespiteTheGate_SaysWhoAndWhy_AnOrdinaryRunSaysNothing()
    {
        var overridden = new PipelineRunDto
        {
            Id = 940,
            GateOverrideBy = "alice",
            GateOverrideAt = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc),
            GateOverrideWarnings = [new DeploymentGateWarningDto { Kind = DeploymentGateWarningKind.NoGrade }]
        };

        var banner = Render<PipelineRunGateOverrideAlert>(p => p.Add(x => x.Run, overridden));
        var ordinary = Render<PipelineRunGateOverrideAlert>(p => p.Add(x => x.Run, new PipelineRunDto { Id = 941 }));

        var alert = banner.Find("[data-testid=run-gate-override]");
        Assert.Contains("GateOverrideRunBadge", alert.TextContent, StringComparison.Ordinal);
        Assert.Empty(ordinary.FindAll("[data-testid=run-gate-override]"));
    }
}
