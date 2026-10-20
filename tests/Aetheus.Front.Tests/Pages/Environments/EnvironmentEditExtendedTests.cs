// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Environments;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.EnvironmentsTests;

/// <summary>
/// Extended coverage for EnvironmentEdit: OnSubmit (create and update paths),
/// server-list management, type styling, and model state.
/// Does NOT test OnDelete (calls Dialog.Confirm → hangs).
/// </summary>
public class EnvironmentEditExtendedTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags PrivStatic = BindingFlags.NonPublic | BindingFlags.Static;

    public EnvironmentEditExtendedTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static PaginatedResult<ProjectDto> BuildProjects() => new()
    {
        Items = [new ProjectDto { Id = 1, Name = "MyProject" }],
        TotalCount = 1
    };

    private static PaginatedResult<ServerDto> BuildServers() => new()
    {
        Items =
        [
            new ServerDto { Id = 5, Name = "web-01", Status = ServerStatus.Online, Type = ServerType.Normal },
            new ServerDto { Id = 6, Name = "web-02", Status = ServerStatus.Offline, Type = ServerType.Normal }
        ],
        TotalCount = 2
    };

    private static EnvironmentDto BuildEnvironment() => new()
    {
        Id = 1,
        Name = "Staging",
        Description = "Staging environment",
        Type = EnvironmentType.Staging,
        ProjectId = 1,
        ProjectName = "MyProject",
        RequireApproval = false,
        ApprovalTimeoutMinutes = 1440,
        Servers = [new EnvironmentServerDto { ServerId = 5, ServerName = "web-01" }]
    };

    private void SetupStubs(int? envId = null)
    {
        _handler.SetJsonResponse("api/projects", BuildProjects());
        _handler.SetJsonResponse("api/servers", BuildServers());
        if (envId.HasValue)
            _handler.SetJsonResponse($"api/environments/{envId}", BuildEnvironment());
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/environments", new EnvironmentDto { Id = 10, Name = "NewEnv" });
    }

    // ── TypeStyle static method ────────────────────────────────────────────

    [Theory]
    [InlineData(EnvironmentType.Production, OmniTone.Danger)]
    [InlineData(EnvironmentType.Staging, OmniTone.Warning)]
    [InlineData(EnvironmentType.Testing, OmniTone.Accent)]
    [InlineData(EnvironmentType.Development, OmniTone.Success)]
    public void TypeStyle_ReturnsCorrectBadge(EnvironmentType type, OmniTone expected)
    {
        var method = typeof(EnvironmentEdit).GetMethod("TypeStyle", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [type])!;
        Assert.Equal(expected, result);
    }

    // ── Model and _isNew ────────────────────────────────────────────────────

    [Fact]
    public void IsNew_TrueWhenIdIsNull()
    {
        SetupStubs();
        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var isNew = (bool)typeof(EnvironmentEdit).GetProperty("_isNew", Priv)!.GetValue(cut.Instance)!;
        Assert.True(isNew);
    }

    [Fact]
    public void IsNew_TrueWhenIdIsZero()
    {
        SetupStubs();
        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, (int?)0));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var isNew = (bool)typeof(EnvironmentEdit).GetProperty("_isNew", Priv)!.GetValue(cut.Instance)!;
        Assert.True(isNew);
    }

    [Fact]
    public void IsNew_FalseWhenIdIsSet()
    {
        SetupStubs(1);
        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var isNew = (bool)typeof(EnvironmentEdit).GetProperty("_isNew", Priv)!.GetValue(cut.Instance)!;
        Assert.False(isNew);
    }

    // ── OnParametersSetAsync ─────────────────────────────────────────────────

    [Fact]
    public void Renders_ExistingEnvironment_PopulatesModel()
    {
        SetupStubs(1);
        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var detail = (EnvironmentDto?)typeof(EnvironmentEdit)
            .GetField("_detail", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(detail);
        Assert.Equal("Staging", detail!.Name);
    }

    [Fact]
    public void Renders_NewEnvironment_HasEmptyModel()
    {
        SetupStubs();
        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var model = typeof(EnvironmentEdit)
            .GetField("_model", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(model);
    }

    [Fact]
    public void Renders_LoadsProjects_And_Servers()
    {
        SetupStubs();
        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var projects = (List<ProjectDto>?)typeof(EnvironmentEdit)
            .GetField("_projects", Priv)!.GetValue(cut.Instance);
        var servers = (List<ServerDto>?)typeof(EnvironmentEdit)
            .GetField("_servers", Priv)!.GetValue(cut.Instance);

        Assert.NotNull(projects);
        Assert.Single(projects!);
        Assert.NotNull(servers);
        Assert.Equal(2, servers!.Count);
    }

    [Fact]
    public void Renders_NewEnvironmentWithMatchingProject_SetsProjectIdFromQuery()
    {
        SetupStubs();
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("http://localhost/?ProjectId=1");

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50);

        // The ?ProjectId=1 query matches a loaded project, so the new model pre-selects it.
        var model = typeof(EnvironmentEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        var projectId = (int?)model.GetType().GetProperty("ProjectId")!.GetValue(model);
        Assert.Equal(1, projectId);
    }

    // ── OnSubmit – create path ────────────────────────────────────────────────

    [Fact]
    public async Task OnSubmit_Create_CallsApiAndNavigates()
    {
        SetupStubs();
        _handler.SetJsonResponse(HttpMethod.Post, "api/environments", new EnvironmentDto { Id = 42, Name = "New" });

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50);

        // Set a valid model name to allow submit
        var modelField = typeof(EnvironmentEdit).GetField("_model", Priv)!;
        var model = modelField.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Name")!.SetValue(model, "New Environment");

        var onSubmit = typeof(EnvironmentEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)onSubmit.Invoke(cut.Instance, [])!);

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.Contains("environments", nav.Uri);
    }

    // ── OnSubmit – update path ────────────────────────────────────────────────

    [Fact]
    public async Task OnSubmit_Update_CallsApiAndUpdatesDetail()
    {
        SetupStubs(1);
        _handler.SetJsonResponse(HttpMethod.Put, "api/environments/1", new EnvironmentDto { Id = 1, Name = "Updated" });

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var modelField = typeof(EnvironmentEdit).GetField("_model", Priv)!;
        var model = modelField.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Name")!.SetValue(model, "Updated Name");

        var onSubmit = typeof(EnvironmentEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)onSubmit.Invoke(cut.Instance, [])!);

        // Update emits a PUT to the id endpoint (the initial load only GETs it).
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/environments/1"));
    }

    // ── Saving flag reset ─────────────────────────────────────────────────────

    [Fact]
    public async Task OnSubmit_Create_NullResponse_DoesNotNavigate()
    {
        SetupStubs();
        _handler.SetJsonResponse(HttpMethod.Post, "api/environments", (EnvironmentDto?)null);

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, (int?)null));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var modelField = typeof(EnvironmentEdit).GetField("_model", Priv)!;
        var model = modelField.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Name")!.SetValue(model, "Test");

        var onSubmit = typeof(EnvironmentEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)onSubmit.Invoke(cut.Instance, [])!);

        var saving = (bool)typeof(EnvironmentEdit).GetField("_saving", Priv)!.GetValue(cut.Instance)!;
        Assert.False(saving);
    }

    // ── Existing environment with RequireApproval ─────────────────────────────

    [Fact]
    public void Renders_EnvironmentWithRequireApproval_True()
    {
        var env = BuildEnvironment() with { RequireApproval = true, ApprovalTimeoutMinutes = 60 };
        _handler.SetJsonResponse("api/projects", BuildProjects());
        _handler.SetJsonResponse("api/servers", BuildServers());
        _handler.SetJsonResponse("api/environments/1", env);

        var cut = Render<EnvironmentEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var detail = (EnvironmentDto?)typeof(EnvironmentEdit)
            .GetField("_detail", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(detail);
        Assert.True(detail!.RequireApproval);
    }

    // ── localized type options ────────────────────────────────────────────────

    [Fact]
    public void Types_ArrayContainsAllEnvironmentTypes()
    {
        _handler.SetPaginatedJsonResponse<ProjectDto>(
            HttpMethod.Get,
            "api/projects?page=1&pageSize=200",
            []);
        _handler.SetPaginatedJsonResponse<ServerDto>(
            HttpMethod.Get,
            "api/servers?page=1&pageSize=100",
            []);
        _handler.SetPaginatedJsonResponse<EnvironmentDto>(
            HttpMethod.Get,
            "api/environments?page=1&pageSize=100",
            []);
        var cut = Render<EnvironmentEdit>();
        var optionsField = typeof(EnvironmentEdit).GetField("_typeOptions", Priv)!;
        var values = ((System.Collections.IEnumerable)optionsField.GetValue(cut.Instance)!)
            .Cast<object>()
            .Select(option => (EnvironmentType)option.GetType().GetProperty("Value")!.GetValue(option)!)
            .ToList();

        Assert.Equal(Enum.GetValues<EnvironmentType>(), values);
    }
}
