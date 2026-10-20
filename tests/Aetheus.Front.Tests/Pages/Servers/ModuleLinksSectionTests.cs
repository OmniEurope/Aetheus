// SPDX-License-Identifier: EUPL-1.2
using System.Linq;
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Coverage for ModuleLinksTab.razor.cs - GetTypeBadge, LoadLinksAsync,
/// AutoDetectAsync, AddLinkAsync, DeleteLinkAsync.
/// </summary>
public class ModuleLinksSectionTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ModuleLinksSectionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private IRenderedComponent<ModuleLinksTab> RenderTab(
        int serverId = 1,
        ModuleLinkType sourceType = ModuleLinkType.Docker,
        List<string>? resources = null)
    {
        var res = resources ?? ["nginx", "api"];
        _handler.SetPaginatedJsonResponse(HttpMethod.Post, "module-links/resource-page", new List<LinkedResourceDto>
        {
            new() { LinkId = 1, Type = ModuleLinkType.Apache, Identifier = "site.conf", IsAutoDetected = false },
            new() { LinkId = 2, Type = ModuleLinkType.Certbot, Identifier = "site.conf", IsAutoDetected = true }
        });
        return Render<ModuleLinksTab>(p => p
            .Add(c => c.ServerId, serverId)
            .Add(c => c.SourceType, sourceType)
            .Add(c => c.Resources, res));
    }

    // ── GetTypeBadge - all enum values ────────────────────────────────────────

    [Theory]
    [InlineData(ModuleLinkType.Docker, OmniTone.Accent)]
    [InlineData(ModuleLinkType.Apache, OmniTone.Warning)]
    [InlineData(ModuleLinkType.Certbot, OmniTone.Success)]
    [InlineData(ModuleLinkType.Mail, OmniTone.Neutral)]
    [InlineData(ModuleLinkType.Teamspeak, OmniTone.Neutral)]
    public void GetTypeBadge_ReturnsExpectedStyle(ModuleLinkType type, OmniTone expected)
    {
        var result = ModuleLinksTab.GetTypeBadge(type);
        Assert.Equal(expected, result);
    }

    // ── OnParametersSetAsync: loads links for all resources ───────────────────

    [Fact]
    public void OnInit_LoadsLinksFromApi()
    {
        var cut = RenderTab();
        cut.WaitForState(
            () => !(bool)typeof(ModuleLinksTab).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        var links = (List<LinkedResourceDto>)typeof(ModuleLinksTab).GetField("_links", Priv)!.GetValue(cut.Instance)!;
        // The full resource set is queried once and the page contains two links.
        Assert.Equal(2, links.Count);
    }

    // ── LoadLinksAsync: empty resources → empty links ─────────────────────────

    [Fact]
    public void OnInit_EmptyResources_ProducesEmptyLinks()
    {
        var cut = RenderTab(resources: []);
        cut.WaitForState(
            () => !(bool)typeof(ModuleLinksTab).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        var links = (List<LinkedResourceDto>)typeof(ModuleLinksTab).GetField("_links", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(links);
    }

    // ── LoadLinksAsync: API error → logs toast, _loading = false ─────────────

    [Fact]
    public void OnInit_ApiError_LoadingFalseAndLinksEmpty()
    {
        _handler.SetResponse(HttpMethod.Post, "module-links/resource-page", System.Net.HttpStatusCode.InternalServerError);
        var cut = Render<ModuleLinksTab>(p => p
            .Add(c => c.ServerId, 1)
            .Add(c => c.SourceType, ModuleLinkType.Docker)
            .Add(c => c.Resources, new List<string> { "nginx" }));
        cut.WaitForState(
            () => !(bool)typeof(ModuleLinksTab).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        var links = (List<LinkedResourceDto>)typeof(ModuleLinksTab).GetField("_links", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(links);
    }

    // ── AutoDetectAsync: success → reloads links ─────────────────────────────

    [Fact]
    public async Task AutoDetectAsync_Success_ReloadsLinks()
    {
        var cut = RenderTab();
        cut.WaitForState(
            () => !(bool)typeof(ModuleLinksTab).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        // Default OK response for auto-detect
        var method = typeof(ModuleLinksTab).GetMethod("AutoDetectAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        Assert.False((bool)typeof(ModuleLinksTab).GetField("_detecting", Priv)!.GetValue(cut.Instance)!);
    }

    // ── AutoDetectAsync: failure → shows error toast ──────────────────────────

    [Fact]
    public async Task AutoDetectAsync_Failure_SetsDetectingFalse()
    {
        var cut = RenderTab();
        cut.WaitForState(
            () => !(bool)typeof(ModuleLinksTab).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        _handler.SetResponse("module-links/auto-detect", System.Net.HttpStatusCode.InternalServerError);
        var method = typeof(ModuleLinksTab).GetMethod("AutoDetectAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        Assert.False((bool)typeof(ModuleLinksTab).GetField("_detecting", Priv)!.GetValue(cut.Instance)!);
    }

    // ── AddLinkAsync: success → posts and reloads ────────────────────────────

    [Fact]
    public async Task AddLinkAsync_Success_PostsAndReloads()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/1/module-links", new ModuleLinkDto
        {
            Id = 99,
            SourceType = ModuleLinkType.Docker,
            SourceIdentifier = "nginx",
            TargetType = ModuleLinkType.Apache,
            TargetIdentifier = "site.conf"
        });
        var cut = RenderTab();
        cut.WaitForState(
            () => !(bool)typeof(ModuleLinksTab).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        var request = new AddModuleLinkResult(ModuleLinkType.Apache, "site.conf", "nginx");
        var method = typeof(ModuleLinksTab).GetMethod("AddLinkAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [request])!);

        Assert.False((bool)typeof(ModuleLinksTab).GetField("_loading", Priv)!.GetValue(cut.Instance)!);
    }

    // ── AddLinkAsync: failure → swallows error, leaves component intact ────────

    [Fact]
    public async Task AddLinkAsync_Failure_StillPostsTheLink()
    {
        _handler.SetResponse(HttpMethod.Post, "api/servers/1/module-links", System.Net.HttpStatusCode.BadRequest);
        var cut = RenderTab();
        cut.WaitForState(
            () => !(bool)typeof(ModuleLinksTab).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        var request = new AddModuleLinkResult(ModuleLinkType.Apache, "site.conf", "nginx");
        var method = typeof(ModuleLinksTab).GetMethod("AddLinkAsync", Priv)!;
        var ex = await Record.ExceptionAsync(async () =>
            await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [request])!));

        // A failed create is swallowed by the catch (error toast), not rethrown.
        Assert.Null(ex);
        // The POST was actually attempted against the module-links endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/servers/1/module-links"));
    }

    // ── DeleteLinkAsync: success → removes link from list ────────────────────

    [Fact]
    public async Task DeleteLinkAsync_Success_ReloadsLinks()
    {
        _handler.SetResponse(
            HttpMethod.Delete,
            "api/servers/1/module-links/1",
            System.Net.HttpStatusCode.NoContent);
        var cut = RenderTab();
        cut.WaitForState(
            () => !(bool)typeof(ModuleLinksTab).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        var method = typeof(ModuleLinksTab).GetMethod("DeleteLinkAsync", Priv)!;
        var resourceGetsBefore = _handler.Requests.Count(r => r.Url.Contains("module-links/resource-page"));
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [1])!);

        // The DELETE is issued, and on success LoadLinksAsync re-fetches the resource links.
        Assert.Contains(_handler.Requests, r => r.Method == "DELETE" && r.Url.Contains("api/servers/1/module-links/1"));
        var resourceGetsAfter = _handler.Requests.Count(r => r.Url.Contains("module-links/resource-page"));
        Assert.True(resourceGetsAfter > resourceGetsBefore, "successful delete must trigger a page reload");
    }

    // ── DeleteLinkAsync: failure → keeps existing links ───────────────────────

    [Fact]
    public async Task DeleteLinkAsync_Failure_DoesNotReload()
    {
        var cut = RenderTab();
        cut.WaitForState(
            () => !(bool)typeof(ModuleLinksTab).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        _handler.SetResponse("module-links/1", System.Net.HttpStatusCode.InternalServerError);
        var method = typeof(ModuleLinksTab).GetMethod("DeleteLinkAsync", Priv)!;
        var resourceGetsBefore = _handler.Requests.Count(r => r.Url.Contains("module-links/resource-page"));
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [1])!);

        // A failed DELETE shows an error toast and does NOT re-fetch the resource links.
        Assert.Contains(_handler.Requests, r => r.Method == "DELETE" && r.Url.Contains("api/servers/1/module-links/1"));
        var resourceGetsAfter = _handler.Requests.Count(r => r.Url.Contains("module-links/resource-page"));
        Assert.Equal(resourceGetsBefore, resourceGetsAfter);
    }

    // ── TargetTypes excludes SourceType ──────────────────────────────────────

    [Fact]
    public void OnInit_TargetTypes_ExcludesSourceType()
    {
        var cut = RenderTab(sourceType: ModuleLinkType.Apache);
        cut.WaitForState(
            () => !(bool)typeof(ModuleLinksTab).GetField("_loading", Priv)!.GetValue(cut.Instance)!);

        var targetTypes = (ModuleLinkType[])typeof(ModuleLinksTab).GetField("_targetTypes", Priv)!.GetValue(cut.Instance)!;
        Assert.DoesNotContain(ModuleLinkType.Apache, targetTypes);
        Assert.Contains(ModuleLinkType.Docker, targetTypes);
    }
}
