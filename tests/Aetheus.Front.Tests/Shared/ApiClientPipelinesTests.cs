// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;

namespace Aetheus.Front.Tests;

/// <summary>Covers all ApiClient.Pipelines methods.</summary>
public class ApiClientPipelinesTests
{
    private readonly BunitTestHelper.TestHandler _handler = new();
    private readonly ApiClient _api;

    public ApiClientPipelinesTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://test/") };
        _api = new ApiClient(http);

        _handler.SetJsonResponse("api/pipelines/templates/1/export", new byte[] { 1, 2, 3 });
        _handler.SetJsonResponse("api/pipelines/templates/import", new PipelineTemplateDto { Id = 99, Name = "Imported" });
        _handler.SetJsonResponse("api/pipelines/templates/1", new PipelineTemplateDto { Id = 1, Name = "Template1" });
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>
        {
            new() { Id = 1, Name = "Build Template" }
        });
        _handler.SetJsonResponse("api/pipelines/validate", new PipelineYamlDefinition());
        _handler.SetJsonResponse("api/pipelines/runs/1/retry-failed", new PipelineRunDto { Id = 1 });
        _handler.SetJsonResponse("api/pipelines/runs/1", new PipelineRunDto { Id = 1 });
        _handler.SetJsonResponse("api/pipelines/runs", new PipelineRunDto { Id = 1 });
        _handler.SetJsonResponse("api/pipelines/1/dry-run", new DryRunResultDto());
        _handler.SetJsonResponse("api/pipelines/1/preflight", new PipelinePreflightDto());
        _handler.SetJsonResponse("api/pipelines/1/run", new PipelineRunDto { Id = 1 });
        _handler.SetJsonResponse("api/pipelines/1/runs", new PaginatedResult<PipelineRunDto>
        {
            Items = [new PipelineRunDto { Id = 1 }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/pipelines/1", new PipelineDto { Id = 1, Name = "Build" });
        _handler.SetJsonResponse("api/pipelines/favorites", new PipelineFavoritesDto { PipelineIds = [1] });
        _handler.SetJsonResponse("api/pipelines", new PaginatedResult<PipelineDto>
        {
            Items = [new PipelineDto { Id = 1, Name = "Build" }],
            TotalCount = 1
        });
    }

    // --- Pipelines ---

    [Fact]
    public async Task GetPipelinesAsync_ReturnsResult()
    {
        var r = await _api.Pipelines.GetPipelinesAsync();
        Assert.Single(r.Items);
    }

    [Fact]
    public async Task GetPipelinesAsync_WithAllParams_ReturnsResult()
    {
        const string url = "api/pipelines?page=2&pageSize=10&search=build&triggerType=Manual&environmentId=1&projectServerId=2";
        _handler.SetJsonResponse(HttpMethod.Get, url, new PaginatedResult<PipelineDto>
        {
            Items = [new PipelineDto { Id = 4, Name = "build" }],
            TotalCount = 1
        });
        var r = await _api.Pipelines.GetPipelinesAsync(page: 2, pageSize: 10, search: "build",
            triggerType: PipelineTriggerType.Manual, environmentId: 1, projectServerId: 2);
        Assert.Equal(4, Assert.Single(r.Items).Id);
        AssertLastRequest(HttpMethod.Get, url);
    }

    [Fact]
    public async Task GetPipelineAsync_ReturnsDto()
    {
        var r = await _api.Pipelines.GetPipelineAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(1, r!.Id);
    }

    [Fact]
    public async Task GetPipelineFavoritesAsync_ReturnsFavoriteIds()
    {
        var result = await _api.Pipelines.GetPipelineFavoritesAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal([1], result.PipelineIds);
        AssertLastRequest(HttpMethod.Get, "api/pipelines/favorites");
    }

    [Fact]
    public async Task SetPipelineFavoriteAsync_PutsRequestedState()
    {
        const string url = "api/pipelines/1/favorite";
        _handler.SetJsonResponse(
            HttpMethod.Put, url, new PipelineFavoriteDto { PipelineId = 1, IsFavorite = true });

        var result = await _api.Pipelines.SetPipelineFavoriteAsync(
            1, true, Xunit.TestContext.Current.CancellationToken);

        Assert.True(result!.IsFavorite);
        AssertLastRequest(HttpMethod.Put, url);
        Assert.Contains("\"isFavorite\":true", _handler.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatePipelineAsync_ReturnsOutcome()
    {
        const string url = "api/pipelines";
        _handler.SetJsonResponse(HttpMethod.Post, url, new PipelineDto { Id = 2, Name = "New" });
        var r = await _api.Pipelines.CreatePipelineAsync(new CreatePipelineRequest { Name = "New" }, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(2, r.Value!.Id);
        Assert.Null(r.Error);
        AssertLastRequest(HttpMethod.Post, url);
    }

    [Fact]
    public async Task UpdatePipelineAsync_ReturnsOutcome()
    {
        const string url = "api/pipelines/1";
        _handler.SetJsonResponse(HttpMethod.Put, url, new PipelineDto { Id = 1, Name = "Updated" });
        var r = await _api.Pipelines.UpdatePipelineAsync(1, new UpdatePipelineRequest { Name = "Updated" }, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("Updated", r.Value!.Name);
        Assert.Null(r.Error);
        AssertLastRequest(HttpMethod.Put, url);
    }

    [Fact]
    public async Task DeletePipelineAsync_ReturnsStatus()
    {
        const string url = "api/pipelines/1";
        _handler.SetResponse(HttpMethod.Delete, url, HttpStatusCode.NoContent);
        var r = await _api.Pipelines.DeletePipelineAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.True(r.Success);
        AssertLastRequest(HttpMethod.Delete, url);
    }

    [Fact]
    public async Task TriggerPipelineRunAsync_ReturnsOutcome()
    {
        const string url = "api/pipelines/1/run";
        _handler.SetJsonResponse(HttpMethod.Post, url, new PipelineRunDto { Id = 17 });
        var r = await _api.Pipelines.TriggerPipelineRunAsync(1, ct: Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(17, r.Value!.Id);
        AssertLastRequest(HttpMethod.Post, url);
        var body = JsonSerializer.Deserialize<Dictionary<string, string>>(_handler.LastRequestBody!);
        var key = Assert.Contains("AETHEUS_RUN_IDEMPOTENCY_KEY", body!);
        Assert.Equal(32, key.Length);
        Assert.All(key, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Fact]
    public async Task PreflightPipelineAsync_ReturnsOutcome()
    {
        const string url = "api/pipelines/1/preflight";
        _handler.SetJsonResponse(HttpMethod.Post, url, new PipelinePreflightDto { Warnings = ["verified"] });
        var r = await _api.Pipelines.PreflightPipelineAsync(1, ct: Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("verified", Assert.Single(r.Value!.Warnings));
        AssertLastRequest(HttpMethod.Post, url);
    }

    /// <summary>R-20: the preview needs the parameters the run will use (the production server among them),
    /// so the preflight request carries them.</summary>
    [Fact]
    public async Task PreflightPipelineAsync_SendsTheChosenParameters()
    {
        const string url = "api/pipelines/1/preflight";
        _handler.SetJsonResponse(HttpMethod.Post, url, new PipelinePreflightDto());

        await _api.Pipelines.PreflightPipelineAsync(1, parameters: new Dictionary<string, string> { ["deployServer"] = "web-01" },
            ct: Xunit.TestContext.Current.CancellationToken);

        AssertLastRequest(HttpMethod.Post, url);
        using var body = JsonDocument.Parse(_handler.LastRequestBody!);
        Assert.Equal("web-01", body.RootElement.GetProperty("parameters").GetProperty("deployServer").GetString());
    }

    [Fact]
    public async Task RetryFailedStepsAsync_ReturnsDto()
    {
        const string url = "api/pipelines/runs/1/retry-failed";
        _handler.SetJsonResponse(HttpMethod.Post, url, new PipelineRunDto { Id = 1 });
        var r = await _api.Pipelines.RetryFailedStepsAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(1, r!.Id);
        AssertLastRequest(HttpMethod.Post, url);
    }

    [Fact]
    public async Task DryRunPipelineAsync_ReturnsResult()
    {
        const string url = "api/pipelines/1/dry-run";
        _handler.SetJsonResponse(HttpMethod.Post, url, new DryRunResultDto { ResolvedVariables = new() { ["KEY"] = "val" } });
        var r = await _api.Pipelines.DryRunPipelineAsync(1, new Dictionary<string, string> { ["KEY"] = "val" }, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("val", r!.ResolvedVariables["KEY"]);
        AssertLastRequest(HttpMethod.Post, url);
    }

    [Fact]
    public async Task DryRunPipelineAsync_NullVars_ReturnsResult()
    {
        const string url = "api/pipelines/1/dry-run";
        _handler.SetJsonResponse(HttpMethod.Post, url, new DryRunResultDto { Warnings = ["none"] });
        var r = await _api.Pipelines.DryRunPipelineAsync(1, ct: Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("none", Assert.Single(r!.Warnings));
        AssertLastRequest(HttpMethod.Post, url);
    }

    [Fact]
    public async Task GetPipelineTemplatesAsync_ReturnsCachedList()
    {
        var r1 = await _api.PipelineTemplates.GetPipelineTemplatesAsync();
        Assert.Single(r1);

        // Second call should use cache
        var r2 = await _api.PipelineTemplates.GetPipelineTemplatesAsync();
        Assert.Same(r1, r2);
    }

    [Fact]
    public async Task GetPipelineTemplatesAsync_AfterInvalidate_RefetchesList()
    {
        var r1 = await _api.PipelineTemplates.GetPipelineTemplatesAsync();
        _api.PipelineTemplates.InvalidateTemplateCache();
        var r2 = await _api.PipelineTemplates.GetPipelineTemplatesAsync();
        Assert.NotSame(r1, r2);
    }

    [Fact]
    public void InvalidateTemplateCache_ClearsCache()
    {
        // Should not throw even if called before cache is populated
        _api.PipelineTemplates.InvalidateTemplateCache();
    }

    [Fact]
    public async Task GetPipelineTemplateAsync_ReturnsDto()
    {
        var r = await _api.PipelineTemplates.GetPipelineTemplateAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("Template1", r!.Name);
    }

    [Fact]
    public async Task CreatePipelineTemplateAsync_CacheIsInvalidatedAfterCreate()
    {
        // Build a fresh ApiClient with a handler that returns a template DTO for any POST to templates
        var handler2 = new BunitTestHelper.TestHandler();
        handler2.SetJsonResponse("api/pipelines/templates/1", new PipelineTemplateDto { Id = 1, Name = "T1" });
        handler2.SetJsonResponse("api/pipelines/templates", new PipelineTemplateDto { Id = 5, Name = "NewTemplate" });
        var http2 = new HttpClient(handler2) { BaseAddress = new Uri("http://test/") };
        var api2 = new ApiClient(http2);

        handler2.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto> { new() { Id = 1, Name = "Cached" } });
        await api2.PipelineTemplates.GetPipelineTemplatesAsync();
        // The cache moved into the sub-client that owns it; it is still the only state in the client.
        var cacheField = typeof(Aetheus.Front.Components.Shared.PipelineTemplatesApi).GetField("_templateCache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.NotNull(cacheField.GetValue(api2.PipelineTemplates));

        handler2.SetJsonResponse(HttpMethod.Post, "api/pipelines/templates", new PipelineTemplateDto { Id = 5, Name = "NewTemplate" });

        // Create invalidates the cache
        var r = await api2.PipelineTemplates.CreatePipelineTemplateAsync(new CreatePipelineTemplateRequest { Name = "NewTemplate" }, Xunit.TestContext.Current.CancellationToken);

        // Cache should be null again after create
        Assert.Equal(5, r!.Id);
        Assert.Null(cacheField.GetValue(api2.PipelineTemplates));
    }

    [Fact]
    public async Task UpdatePipelineTemplateAsync_InvalidatesCache()
    {
        // Pre-populate cache
        await _api.PipelineTemplates.GetPipelineTemplatesAsync();

        _handler.SetJsonResponse(HttpMethod.Put, "api/pipelines/templates/1",
            new PipelineTemplateDto { Id = 1, Name = "Updated Template" });
        var r = await _api.PipelineTemplates.UpdatePipelineTemplateAsync(1, new UpdatePipelineTemplateRequest { Name = "Updated Template" }, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("Updated Template", r!.Name);
        AssertLastRequest(HttpMethod.Put, "api/pipelines/templates/1");
        Assert.Null(typeof(Aetheus.Front.Components.Shared.PipelineTemplatesApi).GetField("_templateCache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_api.PipelineTemplates));
    }

    [Fact]
    public async Task DeletePipelineTemplateAsync_ReturnsStatus_AndInvalidatesCache()
    {
        // Pre-populate cache
        await _api.PipelineTemplates.GetPipelineTemplatesAsync();

        _handler.SetResponse(HttpMethod.Delete, "api/pipelines/templates/1", HttpStatusCode.NoContent);
        var r = await _api.PipelineTemplates.DeletePipelineTemplateAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.True(r.Success);
        AssertLastRequest(HttpMethod.Delete, "api/pipelines/templates/1");
        Assert.Null(typeof(Aetheus.Front.Components.Shared.PipelineTemplatesApi).GetField("_templateCache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_api.PipelineTemplates));
    }

    [Fact]
    public async Task GetPipelineRunsAsync_ReturnsItems()
    {
        var r = await _api.Pipelines.GetPipelineRunsAsync(1);
        Assert.Single(r);
    }

    [Fact]
    public async Task GetActivePipelineRunsAsync_ForwardsProjectScope()
    {
        _handler.SetJsonResponse("api/pipelines/runs/active", new List<PipelineRunDto>
        {
            new() { Id = 12, PipelineId = 3, PipelineName = "CI", Status = PipelineStatus.Running }
        });

        var runs = await _api.Pipelines.GetActivePipelineRunsAsync(7, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(12, Assert.Single(runs).Id);
        Assert.Contains(_handler.Requests, request => request.Url.Contains("runs/active?projectId=7"));
    }

    [Fact]
    public async Task GetRecentPipelineRunsAsync_ForwardsProjectScope()
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>
        {
            new() { Id = 13, PipelineId = 3, PipelineName = "CI", Status = PipelineStatus.Success }
        });

        var runs = await _api.Pipelines.GetRecentPipelineRunsAsync(7, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(13, Assert.Single(runs).Id);
        Assert.Contains(_handler.Requests, request => request.Url.Contains("runs/recent?projectId=7"));
    }

    [Fact]
    public async Task GetPipelineRunsPagedAsync_ReturnsPaginatedResult()
    {
        const string url = "api/pipelines/1/runs?page=1&pageSize=10";
        _handler.SetJsonResponse(HttpMethod.Get, url, new PaginatedResult<PipelineRunDto>
        {
            Items = [new PipelineRunDto { Id = 21 }],
            TotalCount = 1
        });
        var r = await _api.Pipelines.GetPipelineRunsPagedAsync(1, page: 1, pageSize: 10);
        Assert.Equal(21, Assert.Single(r.Items).Id);
        AssertLastRequest(HttpMethod.Get, url);
    }

    [Fact]
    public async Task GetPipelineRunAsync_ReturnsDto()
    {
        var r = await _api.Pipelines.GetPipelineRunAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(1, r!.Id);
    }

    [Fact]
    public async Task ValidatePipelineYamlAsync_ReturnsResult()
    {
        const string url = "api/pipelines/validate";
        _handler.SetJsonResponse(HttpMethod.Post, url, new PipelineYamlDefinition { Name = "build" });
        var r = await _api.Packages.ValidatePipelineYamlAsync("name: build\nsteps: []", Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("build", r!.Name);
        AssertLastRequest(HttpMethod.Post, url);
        Assert.Contains("name: build", _handler.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportPipelineTemplateAsync_ReturnsBytes()
    {
        const string url = "api/pipelines/templates/1/export";
        _handler.SetRawResponse(HttpMethod.Get, url, "abc", "application/octet-stream");
        var r = await _api.PipelineTemplates.ExportPipelineTemplateAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("abc", System.Text.Encoding.UTF8.GetString(r!));
        AssertLastRequest(HttpMethod.Get, url);
    }

    [Fact]
    public async Task ImportPipelineTemplateAsync_ReturnsDto()
    {
        const string url = "api/pipelines/templates/import";
        _handler.SetJsonResponse(HttpMethod.Post, url, new PipelineTemplateDto { Id = 99, Name = "Imported" });
        var fileContent = System.Text.Encoding.UTF8.GetBytes("name: Template\nsteps: []");
        var r = await _api.PipelineTemplates.ImportPipelineTemplateAsync(fileContent, "template.yaml", Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(99, r!.Id);
        AssertLastRequest(HttpMethod.Post, url);
    }

    // --- Projects (same partial file) ---

    [Fact]
    public async Task GetProjectsAsync_ReturnsResult()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 1, Name = "Proj1" }],
            TotalCount = 1
        });
        var r = await _api.Projects.GetProjectsAsync(ct: Xunit.TestContext.Current.CancellationToken);
        Assert.Single(r.Items);
    }

    [Fact]
    public async Task GetProjectsAsync_WithAllParams_ReturnsResult()
    {
        const string url = "api/projects?page=2&pageSize=5&search=web&projectStatus=Active";
        _handler.SetJsonResponse(HttpMethod.Get, url, new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 7, Name = "web" }],
            TotalCount = 1
        });
        var r = await _api.Projects.GetProjectsAsync(
            page: 2,
            pageSize: 5,
            search: "web",
            status: ProjectStatus.Active,
            ct: Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(7, Assert.Single(r.Items).Id);
        AssertLastRequest(HttpMethod.Get, url);
    }

    [Fact]
    public async Task GetProjectDetailAsync_ReturnsDto()
    {
        const string url = "api/projects/1";
        _handler.SetJsonResponse(HttpMethod.Get, url, new ProjectDetailDto { Id = 1, Name = "Proj1" });
        var r = await _api.Projects.GetProjectDetailAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("Proj1", r!.Name);
        AssertLastRequest(HttpMethod.Get, url);
    }

    [Fact]
    public async Task CreateProjectAsync_ReturnsDto()
    {
        const string url = "api/projects";
        _handler.SetJsonResponse(HttpMethod.Post, url, new ProjectDto { Id = 1, Name = "New Project" });
        var r = await _api.Projects.CreateProjectAsync(new CreateProjectRequest { Name = "New Project" }, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("New Project", r!.Name);
        AssertLastRequest(HttpMethod.Post, url);
    }

    [Fact]
    public async Task UpdateProjectAsync_ReturnsDto()
    {
        const string url = "api/projects/1";
        _handler.SetJsonResponse(HttpMethod.Put, url, new ProjectDto { Id = 1, Name = "Updated" });
        var r = await _api.Projects.UpdateProjectAsync(1, new UpdateProjectRequest { Name = "Updated" }, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("Updated", r!.Name);
        AssertLastRequest(HttpMethod.Put, url);
    }

    [Fact]
    public async Task DeleteProjectAsync_ReturnsStatus()
    {
        const string url = "api/projects/1";
        _handler.SetResponse(HttpMethod.Delete, url, HttpStatusCode.NoContent);
        var r = await _api.Projects.DeleteProjectAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.True(r.Success);
        AssertLastRequest(HttpMethod.Delete, url);
    }

    // --- Releases (same partial file) ---

    [Fact]
    public async Task GetReleasesAsync_ReturnsResult()
    {
        const string url = "api/releases?page=1&pageSize=25";
        _handler.SetJsonResponse(HttpMethod.Get, url, new PaginatedResult<ReleaseDto>
        {
            Items = [new ReleaseDto { Id = 1 }],
            TotalCount = 1
        });
        var r = await _api.Projects.GetReleasesAsync();
        Assert.Equal(1, Assert.Single(r.Items).Id);
        AssertLastRequest(HttpMethod.Get, url);
    }

    [Fact]
    public async Task GetReleasesAsync_WithProjectId_ReturnsResult()
    {
        const string url = "api/releases?page=1&pageSize=25&projectId=5";
        _handler.SetJsonResponse(HttpMethod.Get, url, new PaginatedResult<ReleaseDto>
        {
            Items = [new ReleaseDto { Id = 5, ProjectId = 5 }],
            TotalCount = 1
        });
        var r = await _api.Projects.GetReleasesAsync(projectId: 5);
        Assert.Equal(5, Assert.Single(r.Items).ProjectId);
        AssertLastRequest(HttpMethod.Get, url);
    }

    [Fact]
    public async Task GetReleaseAsync_ReturnsDto()
    {
        const string url = "api/releases/1";
        _handler.SetJsonResponse(HttpMethod.Get, url, new ReleaseDto { Id = 1, Version = "1.0.0" });
        var r = await _api.Projects.GetReleaseAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("1.0.0", r!.Version);
        AssertLastRequest(HttpMethod.Get, url);
    }

    [Fact]
    public async Task SyncReleasesAsync_ReturnsListOrEmpty()
    {
        const string url = "api/releases/sync/1";
        _handler.SetJsonResponse(HttpMethod.Post, url, new List<ReleaseDto> { new() { Id = 1 } });
        var r = await _api.Projects.SyncReleasesAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.Single(r).Id);
        AssertLastRequest(HttpMethod.Post, url);
    }

    [Fact]
    public async Task TriggerReleaseBuildAsync_ReturnsDto()
    {
        const string url = "api/releases/1/build";
        _handler.SetJsonResponse(HttpMethod.Post, url, new ReleaseDto { Id = 1, PipelineRunId = 11 });
        var r = await _api.Projects.TriggerReleaseBuildAsync(1, new TriggerReleaseBuildRequest(), Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(11, r!.PipelineRunId);
        AssertLastRequest(HttpMethod.Post, url);
    }

    [Fact]
    public async Task RollbackReleaseAsync_ReturnsDto()
    {
        const string url = "api/releases/1/rollback";
        _handler.SetJsonResponse(HttpMethod.Post, url, new ReleaseRollbackDto { Id = 1 });
        var r = await _api.Projects.RollbackReleaseAsync(1, new RollbackReleaseRequest { PipelineId = 1 }, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(1, r!.Id);
        AssertLastRequest(HttpMethod.Post, url);
    }

    [Fact]
    public async Task PromoteReleaseAsync_ReturnsDto()
    {
        const string url = "api/releases/1/promote";
        _handler.SetJsonResponse(HttpMethod.Post, url, new ReleaseDto { Id = 1, Status = ReleaseStatus.Published });
        var r = await _api.Projects.PromoteReleaseAsync(1, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseStatus.Published, r!.Status);
        AssertLastRequest(HttpMethod.Post, url);
    }

    private void AssertLastRequest(HttpMethod method, string relativeUrl)
    {
        var request = _handler.Requests[^1];
        Assert.Equal(method.Method, request.Method);
        Assert.Equal($"http://test/{relativeUrl}", request.Url);
    }
}




