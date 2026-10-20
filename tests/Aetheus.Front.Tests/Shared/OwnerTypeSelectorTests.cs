// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Tests.Shared;

public class OwnerTypeSelectorTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    public OwnerTypeSelectorTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static List<ProjectDto> MakeProjects() =>
    [
        new ProjectDto { Id = 1, Name = "Alpha", Tags = [] },
        new ProjectDto { Id = 2, Name = "Beta", Tags = [] }
    ];

    private void SetupEnvStub(int projectId = 1)
    {
        _handler.SetJsonResponse($"api/environments", new PaginatedResult<EnvironmentDto>
        {
            Items =
            [
                new EnvironmentDto { Id = 10, Name = "Staging", ProjectId = projectId }
            ],
            TotalCount = 1,
            Page = 1,
            PageSize = 100
        });
    }

    private void SetupProjectServersStub(int projectId = 1)
    {
        _handler.SetPaginatedJsonResponse($"api/projects/{projectId}/servers", new List<ProjectServerDto>
        {
            new() { Id = 20, ProjectId = projectId, DisplayName = "App Server", Host = "10.0.0.1", ServerName = "app-srv" }
        });
    }

    [Fact]
    public void Renders_GlobalKind_ByDefault()
    {
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects()));

        // Global kind is the default with no owner parameters bound, and it loads neither
        // environments nor project-servers (those are project-scoped fetches).
        var kindField = typeof(OwnerTypeSelector)
            .GetField("_kind", Priv)!;
        var kind = (OwnerTypeSelector.OwnerKind)kindField.GetValue(cut.Instance)!;
        Assert.Equal(OwnerTypeSelector.OwnerKind.Global, kind);
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("api/environments"));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("/servers"));
    }

    [Fact]
    public void OnParametersSet_WithProjectId_SetsProjectKind()
    {
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects())
            .Add(x => x.ProjectId, 1));

        var kind = (OwnerTypeSelector.OwnerKind)typeof(OwnerTypeSelector)
            .GetField("_kind", Priv)!
            .GetValue(cut.Instance)!;
        Assert.Equal(OwnerTypeSelector.OwnerKind.Project, kind);
    }

    [Fact]
    public void OnParametersSet_WithEnvironmentId_SetsEnvironmentKind()
    {
        _handler.SetJsonResponse("api/environments/10", new EnvironmentDto
        {
            Id = 10,
            Name = "Staging",
            ProjectId = 1
        });
        SetupEnvStub(1);
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects())
            .Add(x => x.EnvironmentId, 10));

        var kind = (OwnerTypeSelector.OwnerKind)typeof(OwnerTypeSelector)
            .GetField("_kind", Priv)!
            .GetValue(cut.Instance)!;
        Assert.Equal(OwnerTypeSelector.OwnerKind.Environment, kind);
    }

    // FRONT-021: a project without environments answers an empty list; that is a loaded list, and the
    // selector must not ask again after every render.
    [Fact]
    public async Task EnvironmentKind_WithAnEmptyEnvironmentList_LoadsItOnce()
    {
        _handler.SetJsonResponse("api/environments/10", new EnvironmentDto { Id = 10, Name = "Staging", ProjectId = 1 });
        _handler.SetPaginatedJsonResponse("api/environments", new List<EnvironmentDto>());
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects())
            .Add(x => x.EnvironmentId, 10));

        // The mocked answers complete at once: whatever the renders queued has run once this item has.
        await cut.InvokeAsync(() => { });
        cut.Render();
        cut.Render();

        Assert.Equal(1, _handler.Requests.Count(r => r.Url.Contains("api/environments?")));
    }

    [Fact]
    public async Task ProjectServerKind_WithAnEmptyServerList_LoadsItOnce()
    {
        var projects = new List<ProjectDto> { new() { Id = 1, Name = "Alpha", Tags = [] } };
        SetupProjectServersStub(1);
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, projects)
            .Add(x => x.ProjectServerId, 20));
        // The owner lookup, then the dropdown's own load once the kind is inferred.
        await _handler.WhenRequestedAsync(r => r.Url.Contains("api/projects/1/servers", StringComparison.Ordinal), 2, "the owner lookup and the list load");
        _handler.SetPaginatedJsonResponse("api/projects/1/servers", new List<ProjectServerDto>());
        var requestsBefore = _handler.Requests.Count(r => r.Url.Contains("api/projects/1/servers"));

        // The user picks the same project again: one reload, then nothing more however often it renders (the
        // old loop re-rendered itself; a parameter re-render would re-run the owner lookup on this URL).
        var method = typeof(OwnerTypeSelector).GetMethod("OnProjectChanged", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [1])!);
        // The mocked answers complete at once: whatever the renders queued has run once this item has.
        await cut.InvokeAsync(() => { });

        Assert.Equal(requestsBefore + 1, _handler.Requests.Count(r => r.Url.Contains("api/projects/1/servers")));
    }

    [Fact]
    public void OnParametersSet_WithProjectServerId_SetsProjectServerKind()
    {
        SetupProjectServersStub(1);
        _handler.SetPaginatedJsonResponse("api/projects/2/servers", new List<ProjectServerDto>());
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects())
            .Add(x => x.ProjectServerId, 20));

        var kind = (OwnerTypeSelector.OwnerKind)typeof(OwnerTypeSelector)
            .GetField("_kind", Priv)!
            .GetValue(cut.Instance)!;
        Assert.Equal(OwnerTypeSelector.OwnerKind.ProjectServer, kind);
    }

    [Fact]
    public async Task OnKindChanged_ClearsSelections()
    {
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects())
            .Add(x => x.ProjectId, 1));

        // Change kind to Global - should clear everything
        var method = typeof(OwnerTypeSelector).GetMethod("OnKindChanged", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [OwnerTypeSelector.OwnerKind.Global])!);

        var kind = (OwnerTypeSelector.OwnerKind)typeof(OwnerTypeSelector)
            .GetField("_kind", Priv)!
            .GetValue(cut.Instance)!;
        Assert.Equal(OwnerTypeSelector.OwnerKind.Global, kind);

        var selectedProjectId = (int?)typeof(OwnerTypeSelector)
            .GetField("_selectedProjectId", Priv)!
            .GetValue(cut.Instance);
        Assert.Null(selectedProjectId);
    }

    [Fact]
    public void ClickingProjectText_KeepsProjectKindAcrossParentCallbackRender()
    {
        var projectId = (int?)null;
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects())
            .Add(x => x.ProjectId, projectId)
            .Add(x => x.ProjectIdChanged, EventCallback.Factory.Create<int?>(this, value => projectId = value)));

        cut.Find("input[value='Project']").Change(true);

        var kind = (OwnerTypeSelector.OwnerKind)typeof(OwnerTypeSelector)
            .GetField("_kind", Priv)!
            .GetValue(cut.Instance)!;
        Assert.Equal(OwnerTypeSelector.OwnerKind.Project, kind);
        Assert.Single(cut.FindAll(".omni-drop-down"));

        cut.Find(".omni-drop-down").Change("1");
        Assert.Equal(2, projectId);
    }

    [Fact]
    public async Task OnProjectChanged_ProjectKind_SetsSelectedProjectId()
    {
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects()));

        // Set kind to Project first
        typeof(OwnerTypeSelector).GetField("_kind", Priv)!
            .SetValue(cut.Instance, OwnerTypeSelector.OwnerKind.Project);

        var method = typeof(OwnerTypeSelector).GetMethod("OnProjectChanged", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [(object)(int?)1])!);

        var selectedProjectId = (int?)typeof(OwnerTypeSelector)
            .GetField("_selectedProjectId", Priv)!
            .GetValue(cut.Instance);
        Assert.Equal(1, selectedProjectId);
    }

    [Fact]
    public async Task OnProjectChanged_EnvironmentKind_LoadsEnvironments()
    {
        SetupEnvStub(1);
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects()));

        typeof(OwnerTypeSelector).GetField("_kind", Priv)!
            .SetValue(cut.Instance, OwnerTypeSelector.OwnerKind.Environment);

        var method = typeof(OwnerTypeSelector).GetMethod("OnProjectChanged", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [(object)(int?)1])!);

        var environments = (List<EnvironmentDto>)typeof(OwnerTypeSelector)
            .GetField("_environments", Priv)!
            .GetValue(cut.Instance)!;
        Assert.NotEmpty(environments);
    }

    [Fact]
    public async Task OnProjectChanged_ProjectServerKind_LoadsProjectServers()
    {
        SetupProjectServersStub(1);
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects()));

        typeof(OwnerTypeSelector).GetField("_kind", Priv)!
            .SetValue(cut.Instance, OwnerTypeSelector.OwnerKind.ProjectServer);

        var method = typeof(OwnerTypeSelector).GetMethod("OnProjectChanged", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [(object)(int?)1])!);

        var projectServers = (List<ProjectServerDto>)typeof(OwnerTypeSelector)
            .GetField("_projectServers", Priv)!
            .GetValue(cut.Instance)!;
        Assert.NotEmpty(projectServers);
    }

    [Fact]
    public async Task OnEnvironmentChanged_InvokesCallback()
    {
        int? capturedEnvId = -1;
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects())
            .Add(x => x.EnvironmentIdChanged, EventCallback.Factory.Create<int?>(this, v => capturedEnvId = v)));

        var method = typeof(OwnerTypeSelector).GetMethod("OnEnvironmentChanged", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [(object)(int?)10])!);

        Assert.Equal(10, capturedEnvId);
    }

    [Fact]
    public async Task OnProjectServerChanged_InvokesCallback()
    {
        int? capturedServerId = -1;
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects())
            .Add(x => x.ProjectServerIdChanged, EventCallback.Factory.Create<int?>(this, v => capturedServerId = v)));

        var method = typeof(OwnerTypeSelector).GetMethod("OnProjectServerChanged", Priv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [(object)(int?)20])!);

        Assert.Equal(20, capturedServerId);
    }

    [Fact]
    public async Task FindProjectForServerAsync_ResolvesOwningProject()
    {
        SetupProjectServersStub(1);
        _handler.SetPaginatedJsonResponse("api/projects/2/servers", new List<ProjectServerDto>());
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects()));

        var method = typeof(OwnerTypeSelector).GetMethod("FindProjectForServerAsync", Priv)!;
        var result = await (Task<int?>)method.Invoke(cut.Instance, [20])!;
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task FindProjectForEnvironmentAsync_ResolvesOwningProject()
    {
        _handler.SetJsonResponse("api/environments/10", new EnvironmentDto
        {
            Id = 10,
            ProjectId = 2,
            Name = "Production"
        });
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects()));

        var method = typeof(OwnerTypeSelector).GetMethod("FindProjectForEnvironmentAsync", Priv)!;
        var result = await (Task<int?>)method.Invoke(cut.Instance, [10])!;
        Assert.Equal(2, result);
    }

    // The parent assigns the owner inside an async load that finishes after the first render, so a
    // selector that decides its mode on render one decides it from a null id. That is what made
    // /pipelines/new?projectId=13 offer "Global" with no project dropdown.
    [Fact]
    public void LateArrivingProjectId_StillInfersProjectKind()
    {
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects()));
        Assert.Equal(OwnerTypeSelector.OwnerKind.Global, ReadKind(cut.Instance));

        cut.Render(p => p
            .Add(x => x.Projects, MakeProjects())
            .Add(x => x.ProjectId, 2));

        Assert.Equal(OwnerTypeSelector.OwnerKind.Project, ReadKind(cut.Instance));
        Assert.Equal(2, ReadSelectedProjectId(cut.Instance));
    }

    // The other half of the contract, and the reason the original code latched at all: choosing a
    // scoped mode makes the parent clear every owner id, and re-inferring from those cleared ids
    // would snap the selector straight back to Global before the user could pick an owner.
    [Fact]
    public async Task ExplicitKindChoice_SurvivesTheParentClearingEveryOwnerId()
    {
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects()));

        var onKindChanged = typeof(OwnerTypeSelector).GetMethod("OnKindChanged", Priv)!;
        await cut.InvokeAsync(() => (Task)onKindChanged.Invoke(
            cut.Instance, [OwnerTypeSelector.OwnerKind.Project])!);

        Assert.Equal(OwnerTypeSelector.OwnerKind.Project, ReadKind(cut.Instance));

        cut.Render(p => p
            .Add(x => x.Projects, MakeProjects())
            .Add(x => x.ProjectId, (int?)null)
            .Add(x => x.EnvironmentId, (int?)null)
            .Add(x => x.ProjectServerId, (int?)null));

        Assert.Equal(OwnerTypeSelector.OwnerKind.Project, ReadKind(cut.Instance));
    }

    // Symmetric: a user who deliberately goes back to Global must not be dragged into Project by a
    // stale id the parent has not cleared yet.
    [Fact]
    public async Task ExplicitReturnToGlobal_IsNotOverriddenByABoundOwnerId()
    {
        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects())
            .Add(x => x.ProjectId, 1));
        Assert.Equal(OwnerTypeSelector.OwnerKind.Project, ReadKind(cut.Instance));

        var onKindChanged = typeof(OwnerTypeSelector).GetMethod("OnKindChanged", Priv)!;
        await cut.InvokeAsync(() => (Task)onKindChanged.Invoke(
            cut.Instance, [OwnerTypeSelector.OwnerKind.Global])!);

        cut.Render(p => p
            .Add(x => x.Projects, MakeProjects())
            .Add(x => x.ProjectId, 1));

        Assert.Equal(OwnerTypeSelector.OwnerKind.Global, ReadKind(cut.Instance));
    }

    private static OwnerTypeSelector.OwnerKind ReadKind(OwnerTypeSelector instance)
        => (OwnerTypeSelector.OwnerKind)typeof(OwnerTypeSelector).GetField("_kind", Priv)!.GetValue(instance)!;

    // For server or environment ownership, the project selector uses the owner's context project
    // rather than the null direct ProjectId.
    [Fact]
    public void EnvironmentOwner_ProjectDropdown_ShowsTheEnvironmentsProject()
    {
        _handler.SetJsonResponse("api/environments/10", new EnvironmentDto { Id = 10, Name = "Staging", ProjectId = 2 });
        SetupEnvStub(2);

        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects())
            .Add(x => x.EnvironmentId, 10));

        cut.WaitForAssertion(() => Assert.Equal(2, ProjectDropDown(cut).Instance.Value));
    }

    [Fact]
    public void ServerOwner_ProjectDropdown_ShowsTheServersProject()
    {
        _handler.SetPaginatedJsonResponse("api/projects/1/servers", new List<ProjectServerDto>());
        SetupProjectServersStub(2);

        var cut = Render<OwnerTypeSelector>(p => p
            .Add(x => x.Projects, MakeProjects())
            .Add(x => x.ProjectServerId, 20));

        cut.WaitForAssertion(() => Assert.Equal(2, ProjectDropDown(cut).Instance.Value));
    }

    [Fact]
    public async Task ChoosingTheProjectOfAnEnvironmentOwner_KeepsItShown()
    {
        SetupEnvStub(1);
        var cut = Render<OwnerTypeSelector>(p => p.Add(x => x.Projects, MakeProjects()));
        await cut.InvokeAsync(() => cut.Find("input[value=Environment]").Change(true));

        await cut.InvokeAsync(() => ProjectDropDown(cut).Instance.ValueChanged.InvokeAsync(1));

        Assert.Equal(1, ProjectDropDown(cut).Instance.Value);
    }

    private static IRenderedComponent<OmniEurope.Blazor.Components.OmniDropDown<int?>> ProjectDropDown(IRenderedComponent<OwnerTypeSelector> cut)
        => cut.FindComponents<OmniEurope.Blazor.Components.OmniDropDown<int?>>().Single(d => d.Instance.Id == "owner-project");

    private static int? ReadSelectedProjectId(OwnerTypeSelector instance)
        => (int?)typeof(OwnerTypeSelector).GetField("_selectedProjectId", Priv)!.GetValue(instance);
}
