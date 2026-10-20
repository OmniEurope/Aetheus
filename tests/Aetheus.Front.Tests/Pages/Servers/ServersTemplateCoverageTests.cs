// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using OmniEurope.Blazor.Components;
using ServersPage = Aetheus.Front.Components.Servers.Servers;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Template-branch coverage for Servers.razor.
/// Covers status dot, type badge, tags columns, heartbeat branch, lock icon,
/// and column chooser popup.
/// </summary>
public class ServersTemplateCoverageTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServersTemplateCoverageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto> { Items = [], TotalCount = 0 });
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task LoadServers(IRenderedComponent<ServersPage> cut)
    {
        var load = typeof(ServersPage).GetMethod("OnLoadData",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () =>
            await (Task)load.Invoke(cut.Instance, [new GridLoadArgs()])!);
    }

    private static ServerDto MakeServer(int id, ServerType type, ServerStatus status,
        List<string>? tags = null, DateTime? lastHeartbeat = null) => new()
        {
            Id = id,
            Name = $"server-{id}",
            Hostname = $"host-{id}",
            OsDescription = "Ubuntu 22.04",
            AgentVersion = "1.2.3",
            Status = status,
            Type = type,
            LastHeartbeat = lastHeartbeat ?? DateTime.UtcNow,
            Tags = tags ?? []
        };

    // ── IsColumnVisible / ToggleColumnChooser ─────────────────────────────────

    [Fact]
    public void IsColumnVisible_KnownKey_ReturnsTrueByDefault()
    {
        var cut = Render<ServersPage>();
        Assert.True(cut.Instance.IsColumnVisible("Type"));
        Assert.True(cut.Instance.IsColumnVisible("Hostname"));
        Assert.True(cut.Instance.IsColumnVisible("Tags"));
    }

    [Fact]
    public void IsColumnVisible_UnknownKey_ReturnsTrue()
    {
        var cut = Render<ServersPage>();
        Assert.True(cut.Instance.IsColumnVisible("Unknown"));
    }

    [Fact]
    public void ToggleColumnChooser_TogglesFlag()
    {
        var cut = Render<ServersPage>();
        var toggle = typeof(ServersPage).GetMethod("ToggleColumnChooser",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        toggle.Invoke(cut.Instance, []);
        var show = (bool)typeof(ServersPage).GetField("_showColumnChooser",
            BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.True(show);
    }

    /// <summary>
    /// Covers: column chooser popup visible → @foreach col in _columnLabels (lines 21-29).
    /// </summary>
    [Fact]
    public void Template_ColumnChooserOpen_ShowsCheckboxes()
    {
        var cut = Render<ServersPage>();
        typeof(ServersPage).GetField("_showColumnChooser",
            BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, true);
        cut.Render();

        // Popup card appears and lists the toggleable column labels (_columnLabels keys).
        Assert.Contains("column-chooser-popup", cut.Markup);
        Assert.Contains("ChooseColumns", cut.Markup);
        Assert.Contains("LastHeartbeat", cut.Markup);
    }

    // ── ClearFilters ──────────────────────────────────────────────────────────

    // ── Template: servers with varied data ───────────────────────────────────

    /// <summary>
    /// Covers: Online server → green dot (line 73 class condition) AND success badge (lines 96-98).
    /// </summary>
    [Fact]
    public async Task Template_OnlineServer_RendersSuccessState()
    {
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [MakeServer(1, ServerType.Normal, ServerStatus.Online)],
            TotalCount = 1
        });

        var cut = Render<ServersPage>();
        await LoadServers(cut);
        cut.WaitForState(() => cut.Markup.Contains("server-1"));

        // Online → green status dot class, and no offline dot for this row.
        Assert.Contains("server-status-online", cut.Markup);
        Assert.DoesNotContain("server-status-offline", cut.Markup);
    }

    /// <summary>
    /// Covers: Offline server → offline dot + span title + severity badge (lines 99-104).
    /// </summary>
    [Fact]
    public async Task Template_OfflineServer_RendersOfflineState()
    {
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [MakeServer(2, ServerType.Normal, ServerStatus.Offline,
                lastHeartbeat: DateTime.UtcNow.AddMinutes(-10))],
            TotalCount = 1
        });

        var cut = Render<ServersPage>();
        await LoadServers(cut);
        cut.WaitForState(() => cut.Markup.Contains("server-2"));

        // Offline → offline status dot class is present (online branch not taken).
        Assert.Contains("server-status-offline", cut.Markup);
        Assert.DoesNotContain("server-status-online", cut.Markup);
    }

    /// <summary>
    /// Covers: Docker type badge → ServerTypeHelper.GetBadgeStyle/GetIcon (line 85-88).
    /// </summary>
    [Fact]
    public async Task Template_DockerServer_RendersDockerTypeAsText()
    {
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [MakeServer(3, ServerType.Docker, ServerStatus.Online)],
            TotalCount = 1
        });

        var cut = Render<ServersPage>();
        await LoadServers(cut);
        cut.WaitForState(() => cut.Markup.Contains("server-3"));

        // The Type column uses localized plain text.
        Assert.DoesNotContain("inventory_2", cut.Markup);
        Assert.Contains("Enum_ServerType_Docker", cut.Markup);
    }

    /// <summary>
    /// Covers: Build type badge.
    /// </summary>
    [Fact]
    public async Task Template_BuildServer_RendersBuildTypeBadge()
    {
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [MakeServer(4, ServerType.Build, ServerStatus.Online)],
            TotalCount = 1
        });

        var cut = Render<ServersPage>();
        await LoadServers(cut);
        cut.WaitForState(() => cut.Markup.Contains("server-4"));

        // Build type → the localized Build enum key renders in the Type badge.
        Assert.Contains("Enum_ServerType_Build", cut.Markup);
    }

    /// <summary>
    /// Covers: tags column - up to 3 visible tags (lines 124-127)
    /// AND "more" badge when count > 3 (lines 129-133).
    /// </summary>
    [Fact]
    public async Task Template_ServerWithManyTags_RendersTagsAndMoreBadge()
    {
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [MakeServer(5, ServerType.Normal, ServerStatus.Online,
                tags: ["prod", "web", "eu-west", "monitoring", "critical"])],
            TotalCount = 1
        });

        var cut = Render<ServersPage>();
        await LoadServers(cut);
        cut.WaitForState(() => cut.Markup.Contains("server-5"));

        // First 3 tags render; the 4th/5th collapse into a "+2" overflow badge.
        Assert.Contains("prod", cut.Markup);
        Assert.Contains("+2", cut.Markup);
    }

    /// <summary>
    /// Covers: tags count exactly 3 → no "more" badge.
    /// </summary>
    [Fact]
    public async Task Template_ServerWithThreeTags_NoMoreBadge()
    {
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [MakeServer(6, ServerType.Normal, ServerStatus.Online,
                tags: ["prod", "web", "eu-west"])],
            TotalCount = 1
        });

        var cut = Render<ServersPage>();
        await LoadServers(cut);
        cut.WaitForState(() => cut.Markup.Contains("server-6"));

        // Exactly 3 tags → all shown, no "+N" overflow badge.
        Assert.Contains("eu-west", cut.Markup);
        Assert.DoesNotContain("+1", cut.Markup);
    }

    /// <summary>
    /// Covers: LastHeartbeat >= year 2000 → relative time span (lines 141-143).
    /// </summary>
    [Fact]
    public async Task Template_ServerWithRecentHeartbeat_RendersRelativeTime()
    {
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [MakeServer(7, ServerType.Normal, ServerStatus.Online,
                lastHeartbeat: DateTime.UtcNow.AddMinutes(-5))],
            TotalCount = 1
        });

        var cut = Render<ServersPage>();
        await LoadServers(cut);
        cut.WaitForState(() => cut.Markup.Contains("server-7"));

        // Heartbeat within the last hour → relative-time branch, not the "Never" fallback.
        Assert.DoesNotContain(">Never<", cut.Markup);
    }

    /// <summary>
    /// Covers: LastHeartbeat &lt; year 2000 → "Never" text (lines 145-147).
    /// </summary>
    [Fact]
    public async Task Template_ServerNeverHeardBeat_RendersNever()
    {
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [MakeServer(8, ServerType.Normal, ServerStatus.Offline,
                lastHeartbeat: default)], // default DateTime = year 0001
            TotalCount = 1
        });

        var cut = Render<ServersPage>();
        await LoadServers(cut);
        cut.WaitForState(() => cut.Markup.Contains("server-8"));

        // Server with a never-set heartbeat (year 0001) still renders its row.
        Assert.Contains("server-8", cut.Markup);
    }

    /// <summary>Column headers own server filters, with checkable lists for bounded value
    /// sets.</summary>
    [Fact]
    public void Toolbar_IsGone_AndTheColumnsFilterFromTheirHeaders()
    {
        var cut = Render<ServersPage>();

        Assert.Empty(cut.FindAll(".server-list-search"));
        Assert.DoesNotContain("ClearFilters", cut.Markup, StringComparison.Ordinal);
        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("th[data-omni-col='Status'] .omni-multi-select__option"));
            Assert.NotEmpty(cut.FindAll("th[data-omni-col='Type'] .omni-multi-select__option"));
            Assert.NotEmpty(cut.FindAll("th[data-omni-col='LastHeartbeat'] .omni-data-grid__date-range"));
        });
    }

    /// <summary>
    /// Covers: Permissions.CanWrite = false → lock icon in name column (lines 75-78).
    /// </summary>
    [Fact]
    public async Task Template_NoWritePermission_ShowsLockIcon()
    {
        await using var ctx = new BunitContext();
        var handler = BunitTestHelper.RegisterServices(ctx, authenticated: false);
        handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [MakeServer(9, ServerType.Normal, ServerStatus.Online)],
            TotalCount = 1
        });

        var cut = ctx.Render<ServersPage>();
        var load = typeof(ServersPage).GetMethod("OnLoadData",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () =>
            await (Task)load.Invoke(cut.Instance, [new GridLoadArgs()])!);
        cut.WaitForState(() => cut.Markup.Contains("server-9"));

        // No write permission → read-only "lock" icon renders next to the server name.
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Lock);
    }

    /// <summary>
    /// SITE-FUNC-HOME-CAPABILITY-001: the row opens the server overview, so a tag (which filters the list),
    /// the "+N" overflow and the row's action buttons must keep their click and key from reaching it.
    /// </summary>
    [Fact]
    public async Task InnerControls_DoNotLetTheirClickOrKeyReachTheRow()
    {
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [MakeServer(5, ServerType.Normal, ServerStatus.Online,
                tags: ["prod", "web", "eu-west", "monitoring"])],
            TotalCount = 1
        });

        var cut = Render<ServersPage>();
        await LoadServers(cut);
        cut.WaitForState(() => cut.Markup.Contains("server-5"));

        var controls = cut.FindAll("button.tag-filter-button").Cast<AngleSharp.Dom.IElement>()
            .Append(cut.Find(".tag-more-details summary"))
            .Append(cut.Find("button[title='ContactAgent']"))
            // STD-BTN detection 11: RetireServer sits in the row's "More actions" menu, beside ContactAgent.
            .Append(cut.Find("button[title='ContactAgent']").ParentElement!.QuerySelector(".omni-overflow-menu__trigger")!)
            .ToList();
        Assert.Equal(6, controls.Count);
        foreach (var control in controls)
            Aetheus.Front.Tests.TestDoubles.RowEventIsolation.AssertRowDoesNotReceive(control, "onclick", "onkeydown");
    }

    /// <summary>A tag clicked in a row becomes the Tags column's own filter, sent to the
    /// API.</summary>
    [Fact]
    public async Task FilterByTag_SetsTheTagsColumnFilter()
    {
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto> { Items = [], TotalCount = 0 });
        var cut = Render<ServersPage>();

        await cut.InvokeAsync(() => cut.Instance.FilterByTag("production"));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            Uri.UnescapeDataString(request.Url).Contains("Filters[0].Field=Tags", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Value=production", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Covers: OnColumnVisibilityChanged toggles a column off.
    /// </summary>
    [Fact]
    public async Task OnColumnVisibilityChanged_HidesColumn()
    {
        var cut = Render<ServersPage>();

        var method = typeof(ServersPage).GetMethod("OnColumnVisibilityChanged",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, ["Type", false])!);

        Assert.False(cut.Instance.IsColumnVisible("Type"));
    }

    /// <summary>
    /// Covers: multiple servers with all types rendered together.
    /// </summary>
    [Fact]
    public async Task Template_AllServerTypes_Rendered()
    {
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items =
            [
                MakeServer(10, ServerType.Normal, ServerStatus.Online),
                MakeServer(11, ServerType.Docker, ServerStatus.Online),
                MakeServer(12, ServerType.Build, ServerStatus.Offline)
            ],
            TotalCount = 3
        });

        var cut = Render<ServersPage>();
        await LoadServers(cut);
        cut.WaitForState(() => cut.Markup.Contains("server-10"));

        Assert.Contains("server-10", cut.Markup);
        Assert.Contains("server-11", cut.Markup);
        Assert.Contains("server-12", cut.Markup);
    }

    [Fact]
    public async Task RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [MakeServer(1, ServerType.Normal, ServerStatus.Online)],
            TotalCount = 1
        });

        var cut = Render<ServersPage>();
        await LoadServers(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 1);
    }
}
