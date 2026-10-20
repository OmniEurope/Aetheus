// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.PackageFeeds;
using Aetheus.Front.Components.PackageRegistry;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Aetheus.Front.Components.VariableLibraries;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

/// <summary> The header filters of the package, pipeline, artifact and variable grids reach their
/// API as generic column filters (<c>Filters[i].Field</c>...), so the server narrows the rows and
/// the total. A grid whose rows are all in memory (the release picker) narrows them itself.
/// </summary>
public sealed class PackagePipelineVariableGridFilterTests : BunitContext
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public PackagePipelineVariableGridFilterTests() => _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);

    private static GridLoadArgs Filtered(params GridFilterDescriptor[] filters) =>
        new() { Skip = 0, Top = 25, Filters = filters };

    private static GridFilterDescriptor Ticked(string property, params string[] values) =>
        new(property, OmniDataGridFilterValues.Join(values), OmniDataGridFilterOperator.In);

    private static GridFilterDescriptor Between(string property, string from, string to) =>
        new(property, from, OmniDataGridFilterOperator.GreaterThanOrEquals, OmniDataGridFilterOperator.LessThan, to);

    private static async Task LoadAsync<T>(IRenderedComponent<T> cut, string method, GridLoadArgs args) where T : Microsoft.AspNetCore.Components.IComponent
    {
        var info = typeof(T).GetMethod(method, Instance)!;
        await cut.InvokeAsync(async () =>
        {
            if (info.Invoke(cut.Instance, [args]) is Task task) await task;
        });
    }

    private void AssertFilterSent(string path, string field, string op)
    {
        Assert.Contains(_handler.Requests, request =>
        {
            var url = Uri.UnescapeDataString(request.Url);
            return url.Contains(path, StringComparison.Ordinal)
                && url.Contains($"Filters[0].Field={field}", StringComparison.Ordinal)
                && url.Contains($"Filters[0].Operator={op}", StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PackageFeeds_TheTypeListReachesTheApi()
    {
        _handler.SetJsonResponse("api/package-feeds", new PaginatedResult<PackageFeedDto>());
        var cut = Render<PackageFeedsAdmin>();

        await LoadAsync(cut, "LoadDataAsync", Filtered(Ticked("FeedType", "NuGet", "Npm")));

        AssertFilterSent("api/package-feeds", "FeedType", "In");
        Assert.Contains(_handler.Requests, request =>
            Uri.UnescapeDataString(request.Url).Contains($"Filters[0].Value=NuGet{GridFilter.ListSeparator}Npm", StringComparison.Ordinal)
            || Uri.UnescapeDataString(request.Url).Contains($"Filters[0].Value=Npm{GridFilter.ListSeparator}NuGet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PackageRegistry_TheUpdatedRangeReachesTheApi()
    {
        _handler.SetJsonResponse("api/package-registry", new PaginatedResult<PackageRegistryPackageDto>());
        var cut = Render<PackageRegistryAdmin>();

        await LoadAsync(cut, "LoadDataAsync", Filtered(Between("UpdatedAt", "2026-09-01T00:00:00", "2026-09-08T00:00:00")));

        AssertFilterSent("api/package-registry", "UpdatedAt", "GreaterThanOrEqual");
        Assert.Contains(_handler.Requests, request =>
            Uri.UnescapeDataString(request.Url).Contains("Filters[0].SecondOperator=LessThan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PipelineRuns_TheStatusListReachesTheApi_AndAnEmptyFilteredPageKeepsTheGrid()
    {
        _handler.SetJsonResponse("api/pipelines/5", new PipelineDto
        {
            Id = 5,
            Name = "Deploy Prod",
            YamlDefinition = "name: deploy\ntrigger: manual\nstages: []",
            TriggerType = PipelineTriggerType.Manual
        });
        _handler.SetJsonResponse("api/pipelines/5/runs", new PaginatedResult<PipelineRunDto>
        {
            Items = [new PipelineRunDto { Id = 1, PipelineId = 5, Status = PipelineStatus.Success }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>());
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        var cut = Render<PipelineEdit>(parameters => parameters.Add(page => page.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("Deploy Prod"));
        _handler.SetJsonResponse("api/pipelines/5/runs?page=1", new PaginatedResult<PipelineRunDto>());

        await LoadAsync(cut, "OnRunsLoadDataAsync", Filtered(Ticked("Status", "Failed", "Cancelled")));
        cut.Render();

        AssertFilterSent("api/pipelines/5/runs", "Status", "In");
        // Send the status list without a second single-status filter that would narrow results
        // again.
        Assert.DoesNotContain(_handler.Requests, request =>
            request.Url.Contains("api/pipelines/5/runs", StringComparison.Ordinal)
            && request.Url.Contains("&status=", StringComparison.Ordinal));
        Assert.DoesNotContain("NoRunsYet", cut.Markup, StringComparison.Ordinal);

        // Without any filter the same empty page is a pipeline that never ran.
        await LoadAsync(cut, "OnRunsLoadDataAsync", Filtered());
        cut.Render();
        Assert.Contains("NoRunsYet", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TemplateVersions_TheCreatedRangeReachesTheApi()
    {
        _handler.SetJsonResponse("api/pipelines/templates/5/versions", new PaginatedResult<PipelineTemplateVersionSummaryDto>());
        var cut = Render<TemplateVersionHistoryDialog>(parameters => parameters
            .Add(dialog => dialog.TemplateId, 5)
            .Add(dialog => dialog.TemplateName, "Build")
            .Add(dialog => dialog.LatestVersion, 1));

        await LoadAsync(cut, "LoadVersionsAsync", Filtered(Between("CreatedAt", "2026-09-01T00:00:00", "2026-09-02T00:00:00")));

        AssertFilterSent("api/pipelines/templates/5/versions", "CreatedAt", "GreaterThanOrEqual");
    }

    [Fact]
    public async Task ProjectArtifacts_TheEnvironmentListReachesTheApi_WithItsValuesServedByTheApi()
    {
        _handler.SetJsonResponse("api/artifacts/project/1", new PaginatedResult<PipelineArtifactDto>());
        _handler.SetJsonResponse("api/artifacts/project/1/filter-values",
            new ProjectArtifactFilterValuesDto { EnvironmentNames = ["prod", "staging"], PipelineNames = ["CI"] });
        var cut = Render<ProjectArtifactsSection>(parameters => parameters.Add(section => section.ProjectId, 1));

        await LoadAsync(cut, "OnLoadData", Filtered(Ticked("EnvironmentName", "prod")));

        AssertFilterSent("api/artifacts/project/1", "EnvironmentName", "In");
        var values = (ProjectArtifactFilterValuesDto)typeof(ProjectArtifactsSection)
            .GetField("_filterValues", Instance)!.GetValue(cut.Instance)!;
        Assert.Equal(["prod", "staging"], values.EnvironmentNames);
    }

    [Fact]
    public async Task VariableLibraries_TheProjectListReachesTheApi_WithItsValuesServedByTheApi()
    {
        _handler.SetJsonResponse("api/variable-libraries", new PaginatedResult<VariableLibraryDto>());
        _handler.SetJsonResponse("api/variable-libraries/filter-values",
            new VariableLibraryFilterValuesDto { ProjectNames = ["Aetheus"] });
        var cut = Render<VariableLibrariesList>();

        await LoadAsync(cut, "OnLoadData", Filtered(Ticked("ProjectName", "Aetheus")));

        AssertFilterSent("api/variable-libraries?", "ProjectName", "In");
        var values = (VariableLibraryFilterValuesDto)typeof(VariableLibrariesList)
            .GetField("_filterValues", Instance)!.GetValue(cut.Instance)!;
        Assert.Equal(["Aetheus"], values.ProjectNames);
    }

    [Fact]
    public async Task Vaults_TheUpdatedRangeReachesTheApi_AndTheProjectValuesAreServedByTheApi()
    {
        _handler.SetJsonResponse("api/vaults", new PaginatedResult<VaultDto>());
        _handler.SetJsonResponse("api/vaults/filter-values", new VaultFilterValuesDto { ProjectNames = ["Aetheus"] });
        var cut = Render<VaultsList>();

        await LoadAsync(cut, "OnLoadData", Filtered(Between("UpdatedAt", "2026-09-01T00:00:00", "2026-09-02T00:00:00")));

        AssertFilterSent("api/vaults?", "UpdatedAt", "GreaterThanOrEqual");
        var values = (VaultFilterValuesDto)typeof(VaultsList)
            .GetField("_filterValues", Instance)!.GetValue(cut.Instance)!;
        Assert.Equal(["Aetheus"], values.ProjectNames);
    }

    [Fact]
    public void VaultsServerScope_OffersTheProjectsOfTheLoadedList()
    {
        _handler.SetJsonResponse("api/servers/3/vaults", new List<VaultDto>
        {
            new() { Id = 1, Name = "a", ProjectName = "Zeta" },
            new() { Id = 2, Name = "b", ProjectName = "alpha" },
            new() { Id = 3, Name = "c" }
        });
        var cut = Render<VaultsList>(parameters => parameters.Add(list => list.ServerId, 3));

        cut.WaitForAssertion(() =>
        {
            var values = (VaultFilterValuesDto)typeof(VaultsList)
                .GetField("_filterValues", Instance)!.GetValue(cut.Instance)!;
            Assert.Equal(["alpha", "Zeta"], values.ProjectNames);
        });
    }

    [Fact]
    public async Task LibraryEntries_TheKeyFilterReachesTheApi()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>());
        _handler.SetJsonResponse("api/variable-libraries/1", new VariableLibraryDetailDto
        {
            Id = 1,
            Name = "lib",
            RowVersion = Guid.NewGuid()
        });
        _handler.SetJsonResponse("api/variable-libraries/1/entries", new PaginatedResult<VariableEntryDto>());
        var cut = Render<VariableLibraryEdit>(parameters => parameters.Add(page => page.Id, 1));

        await LoadAsync(cut, "LoadEntriesAsync",
            Filtered(new GridFilterDescriptor("Key", "DOMAIN", OmniDataGridFilterOperator.Contains)));

        AssertFilterSent("api/variable-libraries/1/entries", "Key", "Contains");
    }

    [Fact]
    public async Task EntryVersions_TheChangeTypeListReachesTheApi()
    {
        _handler.SetJsonResponse("api/variable-libraries/1/entries/2/versions", new PaginatedResult<VariableEntryVersionDto>());
        var cut = Render<VersionHistoryDialog>(parameters => parameters
            .Add(dialog => dialog.LibraryId, 1)
            .Add(dialog => dialog.EntryId, 2));

        await LoadAsync(cut, "LoadDataAsync", Filtered(Ticked("ChangeType", "Deleted")));

        AssertFilterSent("api/variable-libraries/1/entries/2/versions", "ChangeType", "In");
    }

    [Fact]
    public async Task ReleasePicker_TheStatusListNarrowsTheLoadedReleases()
    {
        var cut = Render<ReleasePickerGrid>(parameters => parameters.Add(grid => grid.Releases,
        [
            new ReleaseDto { Id = 1, Version = "1.0.0", Status = ReleaseStatus.Deployed },
            new ReleaseDto { Id = 2, Version = "1.1.0", Status = ReleaseStatus.Failed },
            new ReleaseDto { Id = 3, Version = "1.2.0", Status = ReleaseStatus.Published }
        ]));

        await LoadAsync(cut, "LoadPage", Filtered(Ticked("Status", "Deployed", "Published")));

        var page = (List<ReleaseDto>)typeof(ReleasePickerGrid).GetField("_page", Instance)!.GetValue(cut.Instance)!;
        Assert.Equal(["1.0.0", "1.2.0"], page.Select(release => release.Version));
        Assert.Equal(2, (int)typeof(ReleasePickerGrid).GetField("_count", Instance)!.GetValue(cut.Instance)!);
    }
}
