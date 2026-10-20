// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Organizations;
using Aetheus.Shared.Components.Organizations;
using Bunit;

namespace Aetheus.Front.Tests;

/// <summary>Verifies that realtime broadcasts preserve unsaved general and project edits in
/// OrganizationDetail. Reflection drives the reload guard because this bUnit harness does not
/// establish a SignalR connection.</summary>
public class OrganizationDetailDirtyGuardTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public OrganizationDetailDirtyGuardTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);
    }

    private static readonly OrganizationDetailDto Org = new(
        1, "Acme Corp", "acme", "A test org",
        DateTime.UtcNow, DateTime.UtcNow,
        [],
        [new OrganizationProjectDto(7, "Project 7", ProjectStatus.Active)]);

    private IRenderedComponent<OrganizationDetail> RenderLoaded()
    {
        _handler.SetJsonResponse("api/organizations/1", Org);
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 7, Name = "Project 7" }],
            TotalCount = 1
        });

        return Render<OrganizationDetail>(p => p.Add(x => x.Id, 1));
    }

    private static bool InvokeDirtyCheck(OrganizationDetail instance, string methodName)
    {
        var method = typeof(OrganizationDetail).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (bool)method.Invoke(instance, [])!;
    }

    private static void SetGeneralName(OrganizationDetail instance, string name)
    {
        var generalField = typeof(OrganizationDetail).GetField("_general", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var general = generalField.GetValue(instance)!;
        general.GetType().GetProperty("Name")!.SetValue(general, name);
    }

    [Fact]
    public void IsGeneralTabDirty_FalseImmediatelyAfterLoad()
    {
        var cut = RenderLoaded();

        Assert.False(InvokeDirtyCheck(cut.Instance, "IsGeneralTabDirty"));
    }

    [Fact]
    public void IsGeneralTabDirty_TrueAfterEditingName()
    {
        var cut = RenderLoaded();

        SetGeneralName(cut.Instance, "Renamed while a broadcast could arrive");

        Assert.True(InvokeDirtyCheck(cut.Instance, "IsGeneralTabDirty"));
    }

    [Fact]
    public void IsProjectsTabDirty_FalseImmediatelyAfterLoad()
    {
        var cut = RenderLoaded();

        Assert.False(InvokeDirtyCheck(cut.Instance, "IsProjectsTabDirty"));
    }

    [Fact]
    public void IsProjectsTabDirty_TrueAfterChangingSelection()
    {
        var cut = RenderLoaded();

        var field = typeof(OrganizationDetail).GetField("_selectedProjectIds", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(cut.Instance, new List<int> { 7, 42 });

        Assert.True(InvokeDirtyCheck(cut.Instance, "IsProjectsTabDirty"));
    }

    [Fact]
    public async Task AdminEntityCallback_DirtyGeneralTab_DoesNotReload()
    {
        var cut = RenderLoaded();
        SetGeneralName(cut.Instance, "Unsaved rename");
        _handler.Requests.Clear();

        await InvokeAdminEntityCallbackAsync(cut, AdminEntities.Organization, 1);

        Assert.DoesNotContain(
            _handler.Requests,
            request => request.Method == "GET" && request.Url.Contains("api/organizations/1"));
    }

    [Fact]
    public async Task AdminEntityCallback_CleanMatchingOrganization_Reloads()
    {
        var cut = RenderLoaded();
        _handler.Requests.Clear();

        await InvokeAdminEntityCallbackAsync(cut, AdminEntities.Organization, 1);

        Assert.Contains(
            _handler.Requests,
            request => request.Method == "GET" && request.Url.Contains("api/organizations/1"));
    }

    [Fact]
    public async Task AdminEntityCallback_CleanMatchingOrganization_RefreshesWithoutLoadingScreen()
    {
        var cut = RenderLoaded();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRefresh = new TaskCompletionSource<OrganizationDetailDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/organizations/1", async _ =>
        {
            refreshStarted.TrySetResult();
            return await finishRefresh.Task;
        });

        var callback = InvokeAdminEntityCallbackAsync(cut, AdminEntities.Organization, 1);
        await refreshStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            Xunit.TestContext.Current.CancellationToken);

        Assert.Empty(cut.FindAll(".aetheus-loader"));
        Assert.Contains("Acme Corp", cut.Markup, StringComparison.Ordinal);

        finishRefresh.SetResult(Org);
        await callback;
    }

    private static Task InvokeAdminEntityCallbackAsync(
        IRenderedComponent<OrganizationDetail> cut,
        string entity,
        int id) =>
        cut.InvokeAsync(() => cut.Instance.OnAdminEntityChangedAsync(entity, id, "updated"));
}
