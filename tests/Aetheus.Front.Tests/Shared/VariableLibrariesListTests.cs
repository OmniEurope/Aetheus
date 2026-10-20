// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

// Exercises VariableLibrariesList across global, project and server scopes, using their respective
// paginated and server-library API paths.
public class VariableLibrariesListTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type ListType = typeof(VariableLibrariesList);

    public VariableLibrariesListTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void ServerScope_RendersLibraryNames()
    {
        _handler.SetJsonResponse("api/servers/1/variable-libraries", new List<VariableLibraryDto>
        {
            new() { Id = 1, Name = "Shared Vars", EntryCount = 2 }
        });

        var cut = Render<VariableLibrariesList>(p => p.Add(x => x.ServerId, 1));
        cut.WaitForState(() => cut.Markup.Contains("Shared Vars"));

        Assert.Contains("Shared Vars", cut.Markup);
    }

    [Fact]
    public void ServerScope_Empty_LoadsEmptyList()
    {
        _handler.SetJsonResponse("api/servers/2/variable-libraries", new List<VariableLibraryDto>());

        var cut = Render<VariableLibrariesList>(p => p.Add(x => x.ServerId, 2));
        cut.WaitForState(() =>
            ((List<VariableLibraryDto>?)ListType.GetField("_serverAll", Priv)!.GetValue(cut.Instance)) is not null);

        var all = (List<VariableLibraryDto>)ListType.GetField("_serverAll", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(all);
    }

    [Fact]
    public void GlobalScope_RendersLibraries()
    {
        _handler.SetJsonResponse("api/variable-libraries", new PaginatedResult<VariableLibraryDto>
        {
            Items = [new VariableLibraryDto { Id = 1, Name = "Globals", Description = "Global variables", EntryCount = 3 }],
            TotalCount = 1
        });

        var cut = Render<VariableLibrariesList>();
        cut.WaitForState(() => cut.Markup.Contains("Globals"));

        Assert.Contains("Globals", cut.Markup);
    }

    [Fact]
    public void ProjectScope_RendersLibraries()
    {
        _handler.SetJsonResponse("api/variable-libraries", new PaginatedResult<VariableLibraryDto>
        {
            Items = [new VariableLibraryDto { Id = 2, ProjectId = 7, ProjectName = "Scoped", Name = "ProjLib", EntryCount = 1 }],
            TotalCount = 1
        });

        var cut = Render<VariableLibrariesList>(p => p.Add(x => x.ProjectId, 7));
        cut.WaitForState(() => cut.Markup.Contains("ProjLib"));

        Assert.Contains("ProjLib", cut.Markup);
        // Project column/caption is hidden in project scope.
        Assert.DoesNotContain("Scoped", cut.Markup);
    }

    [Fact]
    public async Task ProjectScope_NewLibrary_NavigatesWithProjectId()
    {
        _handler.SetJsonResponse("api/variable-libraries", new PaginatedResult<VariableLibraryDto> { Items = [], TotalCount = 0 });
        var cut = Render<VariableLibrariesList>(p => p.Add(x => x.ProjectId, 42));

        var method = ListType.GetMethod("NewLibrary", Priv)!;
        await cut.InvokeAsync(() => method.Invoke(cut.Instance, []));

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.Contains("variable-libraries/new", nav.Uri);
        Assert.Contains("projectId=42", nav.Uri);
    }

    [Fact]
    public async Task OnLoadData_ServerScope_PagesInMemory()
    {
        _handler.SetJsonResponse("api/servers/7/variable-libraries", new List<VariableLibraryDto>
        {
            new() { Id = 1, Name = "A" }, new() { Id = 2, Name = "B" }
        });
        var cut = Render<VariableLibrariesList>(p => p.Add(x => x.ServerId, 7));
        var method = ListType.GetMethod("OnLoadData", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [new GridLoadArgs { Skip = 1, Top = 1 }])!);

        Assert.False((bool)ListType.GetField("_loading", Priv)!.GetValue(cut.Instance)!);
        var libraries = (List<VariableLibraryDto>)ListType.GetField("_libraries", Priv)!.GetValue(cut.Instance)!;
        Assert.Single(libraries);
        Assert.Equal("B", libraries[0].Name);
        Assert.Equal(2, (int)ListType.GetField("_totalCount", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task DisposeAsync_Completes()
    {
        _handler.SetJsonResponse("api/servers/1/variable-libraries", new List<VariableLibraryDto>());
        var cut = Render<VariableLibrariesList>(p => p.Add(x => x.ServerId, 1));

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
    }

    [Fact]
    public void RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetJsonResponse("api/servers/1/variable-libraries", new List<VariableLibraryDto>
        {
            new() { Id = 1, Name = "Shared Vars", EntryCount = 2 }
        });

        var cut = Render<VariableLibrariesList>(p => p.Add(x => x.ServerId, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 1);
    }
}
