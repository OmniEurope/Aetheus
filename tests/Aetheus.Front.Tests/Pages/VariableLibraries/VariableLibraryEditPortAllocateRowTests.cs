// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.VariableLibraries;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.VariableLibraries;

/// <summary>R-87: the row menu of an empty <c>PORT_</c> key allocates that key's port. With one reachable
/// server it goes straight to the allocation; otherwise the allocation dialog opens on that key.</summary>
public sealed class VariableLibraryEditPortAllocateRowTests : BunitContext
{
    private const string AllocateUrl = "api/variable-libraries/1/ports/allocate";
    private readonly BunitTestHelper.TestHandler _handler;

    public VariableLibraryEditPortAllocateRowTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/variable-libraries/1", new VariableLibraryDetailDto { Id = 1, Name = "w6.prod", ProjectId = 5 });
        _handler.SetJsonResponse(HttpMethod.Post, AllocateUrl, new List<VariableEntryDto> { new() { Id = 9, Key = "PORT_FRONT", Value = "10031" } });
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private IRenderedComponent<VariableLibraryEdit> Render(params VariableEntryDto[] entries)
    {
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/variable-libraries/1/entries", entries);
        return Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
    }

    private void Targets(bool twoServers = false, int? preselected = 7, PortAllocationBlocker blocker = PortAllocationBlocker.None)
    {
        var servers = new List<PortAllocationTargetDto> { new() { ServerId = 7, ServerName = "vps2577917" } };
        if (twoServers) servers.Add(new PortAllocationTargetDto { ServerId = 8, ServerName = "vps-other" });
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/servers", new PortAllocationTargetsDto
        {
            Servers = servers,
            PreselectedServerId = twoServers ? null : preselected,
            ProjectId = 5,
            Blocker = blocker
        });
    }

    [Fact]
    public void AnEmptyPortKey_OffersAllocateInItsRowMenu()
    {
        var cut = Render(new VariableEntryDto { Id = 1, Key = "PORT_FRONT", Value = string.Empty });

        Assert.NotNull(cut.FindMenuItem("PortAllocateRow"));
    }

    [Theory]
    [InlineData("PORT_FRONT", "10031")]
    [InlineData("DB_HOST", "")]
    public void AKeyThatIsNotAnEmptyPortKey_OffersNoAllocation(string key, string value)
    {
        var cut = Render(new VariableEntryDto { Id = 1, Key = key, Value = value });

        Assert.Null(cut.FindMenuItem("PortAllocateRow"));
    }

    [Fact]
    public async Task WithOneServer_AllocatesTheKeyDirectlyWithoutOpeningADialog()
    {
        Targets();
        var cut = Render(new VariableEntryDto { Id = 1, Key = "PORT_FRONT", Value = string.Empty });

        await cut.InvokeAsync(() => cut.Instance.AllocateEntryPortAsync(new VariableEntryDto { Id = 1, Key = "PORT_FRONT", Value = string.Empty }));

        Assert.Equal(0, Dialog.OpenCount);
        var post = Assert.Single(_handler.RequestDetails, request => request.Method == "POST"
            && request.Url.EndsWith(AllocateUrl, StringComparison.Ordinal));
        Assert.Contains("\"serverId\":7", post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"PORT_FRONT\"", post.Body, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
    }

    [Fact]
    public async Task WithSeveralServers_OpensTheDialogOnThatKey()
    {
        Targets(twoServers: true);
        var cut = Render(new VariableEntryDto { Id = 1, Key = "PORT_FRONT", Value = string.Empty });

        await cut.InvokeAsync(() => cut.Instance.AllocateEntryPortAsync(new VariableEntryDto { Id = 1, Key = "PORT_FRONT", Value = string.Empty }));

        Assert.Equal(typeof(LibraryPortAllocateDialog), Dialog.LastComponent);
        Assert.Equal(new[] { "PORT_FRONT" }, Assert.IsType<string[]>(Dialog.LastParameters!["InitialKeys"]));
        Assert.DoesNotContain(_handler.RequestDetails, request => request.Url.EndsWith(AllocateUrl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheLibraryCannotAllocate_OpensTheDialogThatSaysWhy()
    {
        Targets(blocker: PortAllocationBlocker.NoServer, preselected: null);
        var cut = Render(new VariableEntryDto { Id = 1, Key = "PORT_FRONT", Value = string.Empty });

        await cut.InvokeAsync(() => cut.Instance.AllocateEntryPortAsync(new VariableEntryDto { Id = 1, Key = "PORT_FRONT", Value = string.Empty }));

        Assert.Equal(typeof(LibraryPortAllocateDialog), Dialog.LastComponent);
        Assert.DoesNotContain(_handler.RequestDetails, request => request.Url.EndsWith(AllocateUrl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedDirectAllocation_ShowsTheApisReasonAndAllocatesNothing()
    {
        Targets();
        _handler.SetJsonResponse(
            HttpMethod.Post, AllocateUrl,
            new ApiError { Message = "The allocation window is full." }, System.Net.HttpStatusCode.Conflict);
        var cut = Render(new VariableEntryDto { Id = 1, Key = "PORT_FRONT", Value = string.Empty });

        await cut.InvokeAsync(() => cut.Instance.AllocateEntryPortAsync(new VariableEntryDto { Id = 1, Key = "PORT_FRONT", Value = string.Empty }));

        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger);
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
    }
}
