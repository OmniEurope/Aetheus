// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Audit;
using Aetheus.Front.Resources;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;

namespace Aetheus.Front.Tests.Pages.Audit;

public class AuditLogsRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type PageType = typeof(AuditLogs);

    public AuditLogsRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        _handler.SetJsonResponse("api/audit/actions", new List<string> { "Created", "Updated", "Deleted" });
        _handler.SetJsonResponse("api/audit/entity-types", new List<string> { "Server", "Pipeline", "Project" });
        _handler.SetJsonResponse("api/audit", new PaginatedResult<AuditLogDto>
        {
            Items = [
                new AuditLogDto { Id = 1, Username = "admin", Action = "Created", EntityType = "Server", EntityId = 1, Timestamp = DateTime.UtcNow },
                new AuditLogDto { Id = 2, Username = "user1", Action = "Updated", EntityType = "Pipeline", EntityId = 2, Timestamp = DateTime.UtcNow.AddMinutes(-5) },
                new AuditLogDto { Id = 3, Username = "user2", Action = "Deleted", EntityType = "Project", EntityId = 3, Timestamp = DateTime.UtcNow.AddMinutes(-10) },
                new AuditLogDto { Id = 4, Username = "user3", Action = "Login", EntityType = "User", EntityId = null, Timestamp = DateTime.UtcNow.AddMinutes(-15) }
            ],
            TotalCount = 4
        });
    }

    [Fact]
    public void Renders_WithAdminAuth()
    {
        var cut = Render<AuditLogs>();
        // Admin init loads the filter option lists and the first page of audit rows.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/audit/actions"));
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/audit/entity-types"));
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/audit?page="));
    }

    [Fact]
    public async Task NonAdmin_Redirects_WithoutCrash()
    {
        await using var ctx = new BunitContext();
        BunitTestHelper.RegisterServices(ctx, isAdmin: false);
        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();
        var cut = ctx.Render<AuditLogs>();

        // The non-admin guard redirects to the home route on init; the logs list stays empty.
        Assert.Contains(nav.History, h => h.Uri == "/");
        var logs = (List<AuditLogDto>)PageType.GetField("_logs", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(logs);
    }

    [Fact]
    public async Task LoadData_SetsLogs()
    {
        var cut = Render<AuditLogs>();
        await cut.InvokeAsync(() => Task.CompletedTask);
        var method = PageType.GetMethod("LoadData", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [new GridLoadArgs()])!);
        var logs = (List<AuditLogDto>)PageType.GetField("_logs", Priv)!.GetValue(cut.Instance)!;
        Assert.NotEmpty(logs);
    }

    [Fact]
    public async Task TimestampRange_IsAHeaderFilter_SentToTheServer_AndThePickersAreGone()
    {
        // The Timestamp column sends its date range as a column filter with both bounds.
        var cut = Render<AuditLogs>();
        Assert.Null(PageType.GetField("_dateFrom", Priv));
        Assert.Null(PageType.GetField("_dateTo", Priv));
        var grid = cut.FindComponent<AetheusDataGrid<AuditLogDto>>();

        await cut.InvokeAsync(() => grid.Instance.LoadData.InvokeAsync(new GridLoadArgs
        {
            Filters =
            [
                new GridFilterDescriptor(nameof(AuditLogDto.Timestamp), "2026-09-01T08:00:00Z",
                    OmniDataGridFilterOperator.GreaterThanOrEquals, OmniDataGridFilterOperator.LessThan, "2026-09-02T18:30:00Z")
            ]
        }));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
        {
            var url = Uri.UnescapeDataString(request.Url);
            return url.Contains("api/audit?", StringComparison.Ordinal)
                && url.Contains("Filters[0].Field=Timestamp", StringComparison.Ordinal)
                && url.Contains("Filters[0].Operator=GreaterThanOrEqual", StringComparison.Ordinal)
                && url.Contains("Filters[0].Value=2026-09-01T08:00:00Z", StringComparison.Ordinal)
                && url.Contains("Filters[0].SecondOperator=LessThan", StringComparison.Ordinal)
                && url.Contains("Filters[0].SecondValue=2026-09-02T18:30:00Z", StringComparison.Ordinal)
                && !url.Contains("dateFrom=", StringComparison.Ordinal);
        }));
    }

    /// <summary>Every action takes the colour of the change it records, not only Created / Updated
    /// / Deleted; a failure or a deletion wins over any other word of the name.</summary>
    [Theory]
    [InlineData("Created", OmniTone.Success)]
    [InlineData("Updated", OmniTone.Accent)]
    [InlineData("Deleted", OmniTone.Danger)]
    [InlineData("Login", OmniTone.Success)]
    [InlineData("LoginFailed.InvalidCredentials", OmniTone.Danger)]
    [InlineData("DeletedSecret", OmniTone.Danger)]
    [InlineData("CreatedSecret", OmniTone.Success)]
    [InlineData("RevealedSecret", OmniTone.Warning)]
    [InlineData("RotatedIngestKeyForDeploy", OmniTone.Warning)]
    [InlineData("AgentUpdateConfirmationFailed", OmniTone.Danger)]
    [InlineData("BlockedByPreflight", OmniTone.Danger)]
    [InlineData("Role.PermissionsUpdated", OmniTone.Accent)]
    [InlineData("DomainEvent", OmniTone.Neutral)]
    public void ActionBadge_FollowsTheKindOfChange(string action, OmniTone expected)
    {
        Assert.Equal(expected, AuditActionPresentation.Badge(action));
    }

    /// <summary>An action with no label yet reads as words, never as a resource key.</summary>
    [Theory]
    [InlineData("RotatedIngestKeyForDeploy", "Rotated ingest key for deploy")]
    [InlineData("Role.UserAdded", "Role: user added")]
    [InlineData("AppliedAIPatch", "Applied AI patch")]
    public void ActionWithoutLabel_IsSpelledOut(string action, string expected)
    {
        var localizer = Substitute.For<IStringLocalizer<AppStrings>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>(), resourceNotFound: true));

        Assert.Equal(expected, AuditActionPresentation.Label(localizer, action));
    }

    [Fact]
    public void ActionWithLabel_UsesTheLabel()
    {
        var localizer = Substitute.For<IStringLocalizer<AppStrings>>();
        localizer["AuditAction_DeletedSecret"].Returns(new LocalizedString("AuditAction_DeletedSecret", "Secret supprimé"));

        Assert.Equal("Secret supprimé", AuditActionPresentation.Label(localizer, "DeletedSecret"));
    }

    [Fact]
    public async Task HeaderFilters_FeedTheServerQuery()
    {
        // Column headers narrow the audit query, including checkable action and entity lists.
        var cut = Render<AuditLogs>();
        var grid = cut.FindComponent<AetheusDataGrid<AuditLogDto>>();
        var separator = Aetheus.Shared.Components.Shared.GridFilter.ListSeparator;

        await cut.InvokeAsync(() => grid.Instance.LoadData.InvokeAsync(new GridLoadArgs
        {
            Filters =
            [
                new GridFilterDescriptor(nameof(AuditLogDto.Username), "admin", OmniDataGridFilterOperator.Contains),
                new GridFilterDescriptor(nameof(AuditLogDto.Action), $"Created{separator}Deleted", OmniDataGridFilterOperator.In),
                new GridFilterDescriptor(nameof(AuditLogDto.EntityType), "Server", OmniDataGridFilterOperator.In)
            ]
        }));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
        {
            var url = Uri.UnescapeDataString(request.Url);
            return url.Contains("api/audit?", StringComparison.Ordinal)
                && url.Contains("Filters[0].Field=Username", StringComparison.Ordinal)
                && url.Contains("Filters[0].Operator=Contains", StringComparison.Ordinal)
                && url.Contains("Filters[0].Value=admin", StringComparison.Ordinal)
                && url.Contains("Filters[1].Field=Action", StringComparison.Ordinal)
                && url.Contains("Filters[1].Operator=In", StringComparison.Ordinal)
                && url.Contains($"Filters[1].Value=Created{separator}Deleted", StringComparison.Ordinal)
                && url.Contains("Filters[2].Field=EntityType", StringComparison.Ordinal);
        }));
    }

    [Fact]
    public async Task Dispose_CanBeCalledTwiceWithoutThrowing()
    {
        var cut = Render<AuditLogs>();

        await cut.Instance.DisposeAsync();
        var ex = await Record.ExceptionAsync(async () => await cut.Instance.DisposeAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task LoadData_WithPaginationArgs_SetsTotalCount()
    {
        var cut = Render<AuditLogs>();
        await cut.InvokeAsync(() => Task.CompletedTask);
        var method = PageType.GetMethod("LoadData", Priv)!;
        var args = new GridLoadArgs { Skip = 0, Top = 10 };
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [args])!);
        // _count mirrors the stub's TotalCount (4) for the grid's server-side pager.
        var count = (int)PageType.GetField("_count", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task RefreshData_SetsLoadingFalseAfterCompletion()
    {
        // A new audit entry refreshes the grid quietly through RefreshData.
        var cut = Render<AuditLogs>();
        await cut.InvokeAsync(() => Task.CompletedTask);
        var method = PageType.GetMethod("RefreshData", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        var loading = (bool)PageType.GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loading);
    }
}
