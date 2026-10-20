// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

public sealed class PipelineFleetTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineFleetTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public async Task Page_RendersFreshnessBadgesAndPermissionChanges()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>
        {
            new() { Id = 7, Name = "ci", Version = 2 }
        });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 4, Name = "Toto" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>
        {
            TotalCount = 2,
            Items =
            [
                new() { PipelineId = 1, PipelineName = "current", TemplateName = "ci", PinnedVersion = 2, LatestVersion = 2, Freshness = PipelineFleetFreshness.Current },
                new() { PipelineId = 2, PipelineName = "outdated", TemplateName = "ci", PinnedVersion = 1, LatestVersion = 2, Freshness = PipelineFleetFreshness.Outdated }
            ]
        });

        var cut = Render<PipelineFleet>();
        var load = typeof(PipelineFleet).GetMethod(
            "LoadDataAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)load.Invoke(cut.Instance,
            [new GridLoadArgs { Skip = 0, Top = 25, OrderBy = "PipelineName desc" }])!);
        cut.Render();
        cut.WaitForState(() => cut.Markup.Contains("outdated", StringComparison.Ordinal));
        Assert.Contains("Current", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Outdated", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Update", cut.Markup, StringComparison.Ordinal);
        // The grid requests its first page and the test explicitly requests sorted data; distinct
        // pages may be fetched, but the same page must not be duplicated.
        var fleetUrls = _handler.Requests
            .Where(request => request.Method == "GET"
                && request.Url.Contains("api/pipelines/fleet", StringComparison.Ordinal))
            .Select(request => request.Url)
            .ToList();
        Assert.NotEmpty(fleetUrls);
        Assert.Equal(fleetUrls.Distinct(StringComparer.Ordinal).Count(), fleetUrls.Count);
        Assert.Null(typeof(PipelineFleet).GetField(
            "_groups", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("sortBy=PipelineName", StringComparison.Ordinal)
            && request.Url.Contains("sortDescending=true", StringComparison.Ordinal));

        var projectChanged = typeof(PipelineFleet)
            .GetMethod("OnProjectChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)projectChanged.Invoke(cut.Instance, [4])!);
        Assert.Contains(_handler.Requests, request => request.Url.Contains("projectId=4", StringComparison.Ordinal));

        var permissions = Services.GetRequiredService<PermissionService>();
        await cut.InvokeAsync(() => permissions.SetPermissions([], isAdmin: false));
        cut.WaitForAssertion(() => Assert.DoesNotContain(">Update<", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposeAsync_AwaitsQueuedPermissionRefresh()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>());
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>());
        var cut = Render<PipelineFleet>();
        var permissions = Services.GetRequiredService<PermissionService>();

        await cut.InvokeAsync(() => permissions.SetPermissions([], isAdmin: false));
        await cut.Instance.DisposeAsync();

        var refresh = (Task)typeof(PipelineFleet)
            .GetField("_permissionRefreshTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.True(refresh.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FilterLoadFailure_RemainsVisibleAfterSuccessfulGridLoad()
    {
        // Template-filter value loading failures remain visible.
        _handler.SetResponse(HttpMethod.Get, "api/pipelines/fleet/filter-values", System.Net.HttpStatusCode.ServiceUnavailable);
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>());
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>());
        var cut = Render<PipelineFleet>();
        var load = typeof(PipelineFleet).GetMethod(
            "LoadDataAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await cut.InvokeAsync(() => (Task)load.Invoke(
            cut.Instance, [new GridLoadArgs { Skip = 0, Top = 25 }])!);
        cut.Render();

        Assert.Contains("FiltersLoadError", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(">LoadError<", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>A header filter reaches the fleet API as a column filter, the project picker still
    /// travels as its own parameter.</summary>
    [Fact]
    public async Task HeaderFilter_ReachesTheApi()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>());
        _handler.SetJsonResponse("api/pipelines/fleet/filter-values", new PipelineFleetFilterValuesDto { Templates = ["ci"] });
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>());
        var cut = Render<PipelineFleet>();

        await cut.InvokeAsync(() => cut.Instance.LoadDataAsync(new GridLoadArgs
        {
            Skip = 0,
            Top = 25,
            Filters = [new GridFilterDescriptor("Freshness", "Outdated", OmniDataGridFilterOperator.Equals)]
        }));

        Assert.Contains(_handler.Requests, request =>
            Uri.UnescapeDataString(request.Url).Contains("api/pipelines/fleet?", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Field=Freshness", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Value=Outdated", StringComparison.Ordinal));
        Assert.DoesNotContain("AllTemplates", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateDialog_ShowsResolvedDiffBeforeConfirmationAndBlocksOrphans()
    {
        _handler.SetJsonResponse("fleet-update/preview", new PipelineFleetUpdatePreviewDto
        {
            PipelineId = 2,
            TemplateName = "ci",
            CurrentVersion = 1,
            TargetVersion = 2,
            CurrentResolvedYaml = "name: old",
            TargetResolvedYaml = "name: new",
            OrphanOverrides = ["stage:build/step:removed"]
        });

        var cut = Render<PipelineFleetUpdateDialog>(parameters => parameters
            .Add(component => component.PipelineId, 2)
            .Add(component => component.TargetVersion, 2));
        cut.WaitForState(() => cut.Markup.Contains("OrphanOverridesWarning", StringComparison.Ordinal));

        var diff = cut.FindComponent<MonacoDiffViewer>();
        Assert.Equal("name: old", diff.Instance.OriginalValue);
        Assert.Equal("name: new", diff.Instance.ModifiedValue);
        Assert.True(cut.Find("button[disabled]").TextContent.Contains("ConfirmUpdate", StringComparison.Ordinal));
    }

    [Fact]
    public void PromoteDialog_ShowsEffectiveDiffAndEditableNewVersion()
    {
        _handler.SetJsonResponse("promote-template/preview", new PipelinePromotePreviewDto
        {
            PipelineId = 2,
            TemplateId = 7,
            TemplateName = "ci",
            LatestVersion = 2,
            LatestTemplateYaml = "name: ci\nstages: []",
            EffectivePipelineYaml = "name: ci\nstages:\n  - name: quality"
        });

        var cut = Render<PromotePipelineTemplateDialog>(parameters => parameters
            .Add(component => component.PipelineId, 2));
        cut.WaitForState(() => cut.FindComponents<MonacoDiffViewer>().Count == 1);

        var diff = cut.FindComponent<MonacoDiffViewer>();
        Assert.Equal("name: ci\nstages: []", diff.Instance.OriginalValue);
        Assert.Contains("quality", diff.Instance.ModifiedValue, StringComparison.Ordinal);
        Assert.Equal("name: ci\nstages:\n  - name: quality", cut.FindComponent<MonacoEditor>().Instance.Value);
        Assert.Contains("ChangelogEntry", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("RebaseSourcePipeline", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractDialog_AllowsEditingTemplateParametersAndPinnedPipelineYaml()
    {
        const string sourceYaml = "name: toto\nstages:\n  - name: build";

        var cut = Render<ExtractPipelineTemplateDialog>(parameters => parameters
            .Add(component => component.PipelineId, 3)
            .Add(component => component.SuggestedName, "toto")
            .Add(component => component.SourceYaml, sourceYaml));

        var editors = cut.FindComponents<MonacoEditor>();
        Assert.Equal(2, editors.Count);
        Assert.Equal(sourceYaml, editors[0].Instance.Value);
        Assert.Contains("extends: 'toto@1'", editors[1].Instance.Value, StringComparison.Ordinal);
        Assert.Contains("ExtractionParametersHelp", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractDialog_ReinitializesWhenOnlySourceYamlChanges()
    {
        var cut = Render<ExtractPipelineTemplateDialog>(parameters => parameters
            .Add(component => component.PipelineId, 3)
            .Add(component => component.SuggestedName, "toto")
            .Add(component => component.SourceYaml, "name: first"));

        cut.Render(parameters => parameters
            .Add(component => component.PipelineId, 3)
            .Add(component => component.SuggestedName, "toto")
            .Add(component => component.SourceYaml, "name: second"));

        Assert.Equal("name: second", cut.FindComponents<MonacoEditor>()[0].Instance.Value);
    }

    [Fact]
    public async Task DialogSubmitGuards_BlockConcurrentRequests()
    {
        var extract = Render<ExtractPipelineTemplateDialog>(parameters => parameters
            .Add(component => component.PipelineId, 3)
            .Add(component => component.SuggestedName, "toto")
            .Add(component => component.SourceYaml, "name: toto"));
        typeof(ExtractPipelineTemplateDialog).GetField("_saving",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(extract.Instance, true);
        await InvokePrivateAsync(extract.Instance, "SubmitAsync");

        _handler.SetJsonResponse("fleet-update/preview", new PipelineFleetUpdatePreviewDto
        {
            PipelineId = 4,
            TargetVersion = 2,
            CurrentResolvedYaml = "old",
            TargetResolvedYaml = "new"
        });
        var update = Render<PipelineFleetUpdateDialog>(parameters => parameters
            .Add(component => component.PipelineId, 4)
            .Add(component => component.TargetVersion, 2));
        update.WaitForState(() => update.FindComponents<MonacoDiffViewer>().Count == 1);
        typeof(PipelineFleetUpdateDialog).GetField("_saving",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(update.Instance, true);
        await InvokePrivateAsync(update.Instance, "ConfirmAsync");

        _handler.SetJsonResponse("promote-template/preview", new PipelinePromotePreviewDto
        {
            PipelineId = 5,
            TemplateName = "ci",
            LatestVersion = 1,
            LatestTemplateYaml = "old",
            EffectivePipelineYaml = "new"
        });
        var promote = Render<PromotePipelineTemplateDialog>(parameters => parameters
            .Add(component => component.PipelineId, 5));
        promote.WaitForState(() => promote.FindComponents<MonacoDiffViewer>().Count == 1);
        typeof(PromotePipelineTemplateDialog).GetField("_saving",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(promote.Instance, true);
        await InvokePrivateAsync(promote.Instance, "SubmitAsync");

        Assert.DoesNotContain(_handler.Requests, request => request.Method == "POST"
            && (request.Url.EndsWith("api/pipelines/3/extract-template", StringComparison.Ordinal)
                || request.Url.EndsWith("api/pipelines/4/fleet-update", StringComparison.Ordinal)
                || request.Url.EndsWith("api/pipelines/5/promote-template", StringComparison.Ordinal)));
    }

    [Fact]
    public void PreviewFailures_ShowExplicitRetryAndCloseActions()
    {
        _handler.SetResponse("fleet-update/preview", System.Net.HttpStatusCode.ServiceUnavailable);
        _handler.SetResponse("promote-template/preview", System.Net.HttpStatusCode.ServiceUnavailable);

        var update = Render<PipelineFleetUpdateDialog>(parameters => parameters
            .Add(component => component.PipelineId, 4)
            .Add(component => component.TargetVersion, 2));
        var promote = Render<PromotePipelineTemplateDialog>(parameters => parameters
            .Add(component => component.PipelineId, 5));

        update.WaitForAssertion(() => Assert.Contains("Retry", update.Markup));
        promote.WaitForAssertion(() => Assert.Contains("Retry", promote.Markup));
        Assert.Contains("GoBack", update.Markup);
        Assert.Contains("GoBack", promote.Markup);
    }

    [Theory]
    [InlineData("Forbidden")]
    [InlineData("NotFound")]
    public void PreviewRefusedOrMissing_StatesTheReasonWithoutOfferingRetry(string status)
    {
        var code = Enum.Parse<System.Net.HttpStatusCode>(status);
        _handler.SetResponse("fleet-update/preview", code);
        _handler.SetResponse("promote-template/preview", code);

        var update = Render<PipelineFleetUpdateDialog>(parameters => parameters
            .Add(component => component.PipelineId, 4)
            .Add(component => component.TargetVersion, 2));
        var promote = Render<PromotePipelineTemplateDialog>(parameters => parameters
            .Add(component => component.PipelineId, 5));

        // Neither answer changes if the reader clicks again, so neither may offer a retry. The
        // client flattened every non-2xx to null before, which is what made all three look alike.
        update.WaitForAssertion(() => Assert.DoesNotContain("Retry", update.Markup));
        promote.WaitForAssertion(() => Assert.DoesNotContain("Retry", promote.Markup));
        Assert.Contains("omni-alert--warning", update.Markup, StringComparison.Ordinal);
        Assert.Contains("omni-alert--warning", promote.Markup, StringComparison.Ordinal);
    }

    private static Task InvokePrivateAsync(object instance, string methodName)
    {
        var method = instance.GetType().GetMethod(
            methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)method.Invoke(instance, [])!;
    }

    [Fact]
    public void R2_145_ANewerCatalogueVersion_ReadsAsInformation_NotAsAWarning()
    {
        // A pinned template version is a choice, not a delay, in the fleet and in the dependency grid.
        Assert.Equal(OmniTone.Info, PipelineFleet.FreshnessStyle(PipelineFleetFreshness.Outdated));
        Assert.Equal(OmniTone.Info, Aetheus.Front.Components.Shared.PipelineDependencyGrid.FreshnessStyle(PipelineFleetFreshness.Outdated));
        Assert.Equal(OmniTone.Success, PipelineFleet.FreshnessStyle(PipelineFleetFreshness.Current));
    }
}
