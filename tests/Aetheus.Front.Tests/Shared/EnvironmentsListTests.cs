// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

// Exercises the shared EnvironmentsList in global and project scopes using server-side pagination.
// EnvironmentHelperTests covers type-badge mapping.
public class EnvironmentsListTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type ListType = typeof(EnvironmentsList);

    public EnvironmentsListTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void GlobalScope_RendersEnvironments()
    {
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto>
        {
            Items = [new EnvironmentDto { Id = 1, Name = "Prod", Type = EnvironmentType.Production }],
            TotalCount = 1
        });

        var cut = Render<EnvironmentsList>();
        cut.WaitForState(() => cut.Markup.Contains("Prod"));

        Assert.Contains("Prod", cut.Markup);
    }

    [Fact]
    public void ProjectScope_RendersEnvironments_WithoutProjectColumn()
    {
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto>
        {
            Items = [new EnvironmentDto { Id = 2, ProjectId = 7, ProjectName = "Scoped", Name = "ProjEnv", Type = EnvironmentType.Staging }],
            TotalCount = 1
        });

        var cut = Render<EnvironmentsList>(p => p.Add(x => x.ProjectId, 7));
        cut.WaitForState(() => cut.Markup.Contains("ProjEnv"));

        Assert.Contains("ProjEnv", cut.Markup);
        Assert.DoesNotContain("Scoped", cut.Markup);
    }

    [Fact]
    public async Task OnLoadData_LoadsEnvironments()
    {
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto>
        {
            Items = [new EnvironmentDto { Id = 1, Name = "Dev", Type = EnvironmentType.Development }],
            TotalCount = 1
        });
        var cut = Render<EnvironmentsList>();
        var method = ListType.GetMethod("OnLoadData", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [new GridLoadArgs()])!);

        var envs = (List<EnvironmentDto>)ListType.GetField("_environments", Priv)!.GetValue(cut.Instance)!;
        Assert.Single(envs);
    }

    [Fact]
    public async Task HeaderFilters_AreSentAsColumnFilters()
    {
        // Environment header filters are transmitted as server-side column filters.
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto> { Items = [], TotalCount = 0 });
        var cut = Render<EnvironmentsList>();
        var grid = cut.FindComponent<AetheusDataGrid<EnvironmentDto>>();
        var separator = Aetheus.Shared.Components.Shared.GridFilter.ListSeparator;

        await cut.InvokeAsync(() => grid.Instance.LoadData.InvokeAsync(new GridLoadArgs
        {
            Filters =
            [
                new GridFilterDescriptor(nameof(EnvironmentDto.Name), "abc", OmniDataGridFilterOperator.Contains),
                new GridFilterDescriptor(nameof(EnvironmentDto.Type), $"Production{separator}Staging", OmniDataGridFilterOperator.In),
                new GridFilterDescriptor(nameof(EnvironmentDto.RequireApproval), "True", OmniDataGridFilterOperator.Equals)
            ]
        }));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
        {
            var url = Uri.UnescapeDataString(request.Url);
            return request.Method == "GET"
                && url.Contains("api/environments", StringComparison.Ordinal)
                && url.Contains("Filters[0].Field=Name", StringComparison.Ordinal)
                && url.Contains("Filters[0].Value=abc", StringComparison.Ordinal)
                && url.Contains("Filters[1].Field=Type", StringComparison.Ordinal)
                && url.Contains($"Filters[1].Value=Production{separator}Staging", StringComparison.Ordinal)
                && url.Contains("Filters[2].Field=RequireApproval", StringComparison.Ordinal)
                && url.Contains("Filters[2].Value=True", StringComparison.Ordinal)
                && !url.Contains("search=", StringComparison.Ordinal);
        }));
    }

    [Fact]
    public async Task DuplicateEnvironment_Success_Navigates()
    {
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/environments/1/duplicate", new EnvironmentDto { Id = 99, Name = "Copy" });
        var cut = Render<EnvironmentsList>();

        var method = ListType.GetMethod("DuplicateEnvironment", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [new EnvironmentDto { Id = 1, Name = "Prod" }])!);

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.Contains("environments/99", nav.Uri);
    }

    [Fact]
    public async Task ProjectScope_NewEnvironment_NavigatesWithProjectId()
    {
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto> { Items = [], TotalCount = 0 });
        var cut = Render<EnvironmentsList>(p => p.Add(x => x.ProjectId, 42));

        var method = ListType.GetMethod("NewEnvironment", Priv)!;
        await cut.InvokeAsync(() => method.Invoke(cut.Instance, []));

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.Contains("environments/new", nav.Uri);
        Assert.Contains("projectId=42", nav.Uri);
    }

    [Fact]
    public void OnPermissionsChanged_UpdatesCanWrite()
    {
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto> { Items = [], TotalCount = 0 });
        var cut = Render<EnvironmentsList>();
        ListType.GetField("_canWrite", Priv)!.SetValue(cut.Instance, false);
        ListType.GetMethod("OnPermissionsChanged", Priv)!.Invoke(cut.Instance, []);

        Assert.True((bool)ListType.GetField("_canWrite", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task DisposeAsync_Completes()
    {
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto> { Items = [], TotalCount = 0 });
        var cut = Render<EnvironmentsList>();

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
    }

    [Fact]
    public void RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetJsonResponse("api/environments", new PaginatedResult<EnvironmentDto>
        {
            Items = [new EnvironmentDto { Id = 1, Name = "Prod", Type = EnvironmentType.Production }],
            TotalCount = 1
        });

        var cut = Render<EnvironmentsList>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        // The name link and the duplicate button.
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 2);
    }
}
