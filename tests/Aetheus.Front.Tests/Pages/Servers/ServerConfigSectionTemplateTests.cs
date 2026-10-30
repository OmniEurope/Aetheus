// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerConfigSectionTemplateTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;
    private IRenderedComponent<PageHeaderOutletHost>? _host;

    public ServerConfigSectionTemplateTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private IRenderedComponent<ServerConfigSection> RenderSection(int serverId = 10)
    {
        _handler.SetJsonResponse($"api/servers/{serverId}/configuration/export", "hostname: srv01\npackages: [nginx]");
        _handler.SetJsonResponse($"api/servers/{serverId}/configuration/validate", new ServerConfigValidationResult { IsValid = true, Errors = [] });
        _handler.SetJsonResponse($"api/servers/{serverId}/configuration/preview", new ServerConfigPreviewDto { TaskCount = 2, Changes = [] });
        _handler.SetJsonResponse($"api/servers/{serverId}/configuration/deploy", new ServerConfigDeployResultDto { TasksCreated = 2 });
        // Export and Import sit in the server page header (R-109), so the section is rendered next to the header outlet.
        _host = this.RenderWithPageHeader<ServerConfigSection>(p => p.Add(x => x.ServerId, serverId));
        return _host.FindComponent<ServerConfigSection>();
    }

    [Fact]
    public void Renders_WithServerId()
    {
        var cut = RenderSection();
        // The ServerId parameter is bound and the Export/Import YAML actions render in the page header.
        Assert.Equal(10, cut.Instance.ServerId);
        Assert.Contains("ExportYaml", _host!.Find(".test-outlet").InnerHtml);
        // Import sits in the "More actions" menu beside Export (STD-BTN detection 11).
        Assert.NotNull(_host!.FindMenuItem("ImportYaml"));
    }

    [Fact]
    public async Task ExportConfigAsync_SetsYaml()
    {
        var cut = RenderSection();
        var method = typeof(ServerConfigSection).GetMethod("ExportConfigAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var yaml = (string)typeof(ServerConfigSection).GetField("_configYaml", Priv)!.GetValue(cut.Instance)!;
        // TestHandler serializes string as JSON (adds quotes), so yaml will be the JSON-encoded string
        Assert.NotEmpty(yaml);
    }

    [Fact]
    public async Task ValidateConfigAsync_EmptyYaml_Returns()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configImportYaml", Priv)!.SetValue(cut.Instance, "");

        var method = typeof(ServerConfigSection).GetMethod("ValidateConfigAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var result = typeof(ServerConfigSection).GetField("_configValidation", Priv)!.GetValue(cut.Instance);
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateConfigAsync_WithYaml_SetsValidation()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configImportYaml", Priv)!.SetValue(cut.Instance, "hostname: srv01");

        var method = typeof(ServerConfigSection).GetMethod("ValidateConfigAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var result = (ServerConfigValidationResult?)typeof(ServerConfigSection).GetField("_configValidation", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(result);
        Assert.True(result!.IsValid);
    }

    [Fact]
    public async Task PreviewConfigAsync_EmptyYaml_Returns()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configImportYaml", Priv)!.SetValue(cut.Instance, "");

        var method = typeof(ServerConfigSection).GetMethod("PreviewConfigAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var preview = typeof(ServerConfigSection).GetField("_configPreview", Priv)!.GetValue(cut.Instance);
        Assert.Null(preview);
    }

    [Fact]
    public async Task PreviewConfigAsync_WithYaml_SetsPreview()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configImportYaml", Priv)!.SetValue(cut.Instance, "hostname: srv01");

        var method = typeof(ServerConfigSection).GetMethod("PreviewConfigAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var preview = (ServerConfigPreviewDto?)typeof(ServerConfigSection).GetField("_configPreview", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(preview);
        Assert.Equal(2, preview!.TaskCount);
    }

    [Fact]
    public async Task DeployConfigAsync_EmptyYaml_Returns()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configImportYaml", Priv)!.SetValue(cut.Instance, "");

        var method = typeof(ServerConfigSection).GetMethod("DeployConfigAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var visible = (bool)typeof(ServerConfigSection).GetField("_configImportVisible", Priv)!.GetValue(cut.Instance)!;
        // If yaml was empty, import form state unchanged (still false by default)
        Assert.False(visible);
    }

    [Fact]
    public async Task DeployConfigAsync_WithYaml_ClosesImportPanel()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configImportYaml", Priv)!.SetValue(cut.Instance, "hostname: srv01");
        typeof(ServerConfigSection).GetField("_configImportVisible", Priv)!.SetValue(cut.Instance, true);

        var method = typeof(ServerConfigSection).GetMethod("DeployConfigAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var visible = (bool)typeof(ServerConfigSection).GetField("_configImportVisible", Priv)!.GetValue(cut.Instance)!;
        Assert.False(visible);
    }

    [Theory]
    [InlineData("pull", OmniTone.Success)]
    [InlineData("create", OmniTone.Success)]
    [InlineData("deploy", OmniTone.Success)]
    [InlineData("enable", OmniTone.Success)]
    [InlineData("update", OmniTone.Accent)]
    [InlineData("unchanged", OmniTone.Neutral)]
    [InlineData("remove", OmniTone.Neutral)]
    public void GetChangeBadgeStyle_ReturnsExpected(string action, OmniTone expected)
    {
        var method = typeof(ServerConfigSection).GetMethod("GetChangeBadgeStyle", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [action])!;
        Assert.Equal(expected, result);
    }
}
