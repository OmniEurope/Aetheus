// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ModuleLinksTabTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ModuleLinksTabTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private IRenderedComponent<ModuleLinksTab> RenderTab(int serverId = 1, ModuleLinkType sourceType = ModuleLinkType.Docker, List<string>? resources = null)
    {
        _handler.SetPaginatedJsonResponse(HttpMethod.Post,
            $"api/servers/{serverId}/module-links/resource-page", new List<LinkedResourceDto>());
        return Render<ModuleLinksTab>(p =>
            p.Add(x => x.ServerId, serverId)
             .Add(x => x.SourceType, sourceType)
             .Add(x => x.Resources, resources ?? ["nginx"]));
    }

    [Fact]
    public void Renders_EmptyLinks()
    {
        var cut = RenderTab();
        // OnParametersSetAsync loads links for each resource via the resource endpoint.
        Assert.Contains(_handler.Requests, r =>
            r.Method == "POST" && r.Url.Contains("api/servers/1/module-links/resource-page"));
    }

    [Fact]
    public void Renders_WithLinks()
    {
        _handler.SetPaginatedJsonResponse(HttpMethod.Post, "api/servers/1/module-links/resource-page", new List<LinkedResourceDto>
        {
            new() { LinkId = 1, Type = ModuleLinkType.Apache, Identifier = "default" }
        });
        var cut = Render<ModuleLinksTab>(p =>
            p.Add(x => x.ServerId, 1)
             .Add(x => x.SourceType, ModuleLinkType.Docker)
             .Add(x => x.Resources, new List<string> { "nginx" }));

        var priv = BindingFlags.NonPublic | BindingFlags.Instance;
        cut.WaitForState(() => !(bool)typeof(ModuleLinksTab).GetField("_loading", priv)!.GetValue(cut.Instance)!);
        // The single stubbed Apache link is loaded into _links.
        var links = (List<LinkedResourceDto>)typeof(ModuleLinksTab).GetField("_links", priv)!.GetValue(cut.Instance)!;
        Assert.Single(links);
        Assert.Equal(ModuleLinkType.Apache, links[0].Type);
    }

    [Fact]
    public void Renders_WithMultipleResources()
    {
        _handler.SetPaginatedJsonResponse(HttpMethod.Post, "api/servers/1/module-links/resource-page", new List<LinkedResourceDto>
        {
            new() { LinkId = 1, Type = ModuleLinkType.Apache, Identifier = "site1" },
            new() { LinkId = 2, Type = ModuleLinkType.Certbot, Identifier = "cert1" }
        });
        var cut = Render<ModuleLinksTab>(p =>
            p.Add(x => x.ServerId, 1)
             .Add(x => x.SourceType, ModuleLinkType.Docker)
             .Add(x => x.Resources, new List<string> { "nginx", "redis" }));

        var priv = BindingFlags.NonPublic | BindingFlags.Instance;
        cut.WaitForState(() => !(bool)typeof(ModuleLinksTab).GetField("_loading", priv)!.GetValue(cut.Instance)!);
        // The resource set is sent in one paginated request and returns two unique links.
        var links = (List<LinkedResourceDto>)typeof(ModuleLinksTab).GetField("_links", priv)!.GetValue(cut.Instance)!;
        Assert.Equal(2, links.Count);
    }

    [Fact]
    public async Task OnLoadDataAsync_RequestsSecondServerPage()
    {
        _handler.SetPaginatedJsonResponse(HttpMethod.Post,
            "api/servers/1/module-links/resource-page",
            new List<LinkedResourceDto> { new() { LinkId = 1, Identifier = "first-page" } });
        var cut = Render<ModuleLinksTab>(parameters => parameters
            .Add(component => component.ServerId, 1)
            .Add(component => component.SourceType, ModuleLinkType.Docker)
            .Add(component => component.Resources, new List<string> { "nginx" }));
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        cut.WaitForState(() => !(bool)typeof(ModuleLinksTab)
            .GetField("_loading", flags)!.GetValue(cut.Instance)!);
        _handler.SetPaginatedJsonResponse(HttpMethod.Post,
            "api/servers/1/module-links/resource-page",
            new List<LinkedResourceDto> { new() { LinkId = 26, Identifier = "second-page" } });

        var method = typeof(ModuleLinksTab).GetMethod("OnLoadDataAsync", flags)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance,
            [new GridLoadArgs { Skip = 25, Top = 25 }])!);

        var links = (List<LinkedResourceDto>)typeof(ModuleLinksTab)
            .GetField("_links", flags)!.GetValue(cut.Instance)!;
        Assert.Equal("second-page", Assert.Single(links).Identifier);
        var body = Assert.IsType<string>(_handler.LastRequestBody);
        Assert.Contains("\"page\":2", body);
        Assert.Contains("\"resourceIdentifiers\":[\"nginx\"]", body);
    }

    [Theory]
    [InlineData(ModuleLinkType.Docker, OmniTone.Accent)]
    [InlineData(ModuleLinkType.Apache, OmniTone.Warning)]
    [InlineData(ModuleLinkType.Certbot, OmniTone.Success)]
    public void GetTypeBadge_ReturnsExpected(ModuleLinkType type, OmniTone expected)
    {
        var result = ModuleLinksTab.GetTypeBadge(type);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task AutoDetectAsync_CallsApi()
    {
        _handler.SetPaginatedJsonResponse(HttpMethod.Post, "api/servers/1/module-links/resource-page", new List<LinkedResourceDto>());
        _handler.SetJsonResponse("api/servers/1/module-links/auto-detect", true);
        var cut = RenderTab();
        var method = typeof(ModuleLinksTab).GetMethod("AutoDetectAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);
        // AutoDetect POSTs to the auto-detect endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/1/module-links/auto-detect"));
    }

    [Fact]
    public async Task DeleteLinkAsync_CallsApi()
    {
        _handler.SetResponse(
            HttpMethod.Delete,
            "api/servers/1/module-links/5",
            System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse(HttpMethod.Post, "api/servers/1/module-links/resource-page", new List<LinkedResourceDto>
        {
            new() { LinkId = 5, Type = ModuleLinkType.Apache, Identifier = "site1" }
        });
        var cut = RenderTab();
        var method = typeof(ModuleLinksTab).GetMethod("DeleteLinkAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [5])!);
        // DeleteLink issues a DELETE for the given link id.
        Assert.Contains(_handler.Requests, r => r.Method == "DELETE" && r.Url.Contains("api/servers/1/module-links/5"));
    }

    [Fact]
    public async Task AddLinkAsync_CallsApi()
    {
        // Rule 4: invoke the extracted save logic directly with a dialog result;
        // it performs the same API POST + reload the dialog would trigger.
        _handler.SetPaginatedJsonResponse(HttpMethod.Post, "api/servers/1/module-links/resource-page", new List<LinkedResourceDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/1/module-links",
            new ModuleLinkDto { Id = 10, SourceType = ModuleLinkType.Docker, SourceIdentifier = "nginx", TargetType = ModuleLinkType.Apache, TargetIdentifier = "new" });
        var cut = RenderTab();
        var request = new AddModuleLinkResult(ModuleLinkType.Apache, "site1", "nginx");
        var method = typeof(ModuleLinksTab).GetMethod("AddLinkAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [request])!);
        // AddLink POSTs the new link to the module-links collection endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/servers/1/module-links"));
    }

    [Fact]
    public async Task OpenAddDialog_FiresDialogServiceOnOpen()
    {
        // Rule 1-3: never WaitForState on dialog content; start OpenAsync un-awaited,
        // assert OnOpen fired, then close immediately and await the captured task.
        var cut = RenderTab();
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var opened = false;
        dialog.OnOpen += (_, _, _, _) => opened = true;

        var method = typeof(ModuleLinksTab).GetMethod("OpenAddDialogAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(() => dialog.Close(null));
        await task;

        Assert.True(opened);
    }

    [Fact]
    public void AddModuleLinkDialog_RendersForm_AddWired()
    {
        // Rule 4: render the dialog directly; assert its form renders and Add/Cancel are wired.
        var dlg = Render<AddModuleLinkDialog>(p => p
            .Add(x => x.TargetTypes, new[] { ModuleLinkType.Apache, ModuleLinkType.Certbot })
            .Add(x => x.Resources, new List<string> { "nginx" }));

        // The form renders its three labelled fields, and OnInitialized prefills the
        // first target type + first source resource.
        Assert.Contains("TargetType", dlg.Markup);
        Assert.Contains("SourceResource", dlg.Markup);
        var priv = BindingFlags.NonPublic | BindingFlags.Instance;
        Assert.Equal(ModuleLinkType.Apache, (ModuleLinkType)typeof(AddModuleLinkDialog).GetField("_targetType", priv)!.GetValue(dlg.Instance)!);
        Assert.Equal("nginx", (string)typeof(AddModuleLinkDialog).GetField("_sourceIdentifier", priv)!.GetValue(dlg.Instance)!);
        // The Add button is present and wired (clickable without throwing).
        dlg.FindAll("button").First(b => b.TextContent.Contains("Add")).Click();
    }
}
