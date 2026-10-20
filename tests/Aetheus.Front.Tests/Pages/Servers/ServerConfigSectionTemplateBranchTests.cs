// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Template-branch coverage for ServerConfigSection.razor (28 uncovered lines).
/// Exercises all @if branches: _configYaml non-empty (copy button + yaml card),
/// _configImportVisible (import panel), _configValidation valid/invalid,
/// _configPreview with various action-type badges.
/// </summary>
public class ServerConfigSectionTemplateBranchTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerConfigSectionTemplateBranchTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private IRenderedComponent<ServerConfigSection> RenderSection(int serverId = 1)
    {
        _handler.SetJsonResponse($"api/servers/{serverId}/configuration/export", "hostname: srv01\ntype: Normal");
        _handler.SetJsonResponse($"api/servers/{serverId}/configuration/validate",
            new ServerConfigValidationResult { IsValid = true, Errors = [] });
        _handler.SetJsonResponse($"api/servers/{serverId}/configuration/preview",
            new ServerConfigPreviewDto { TaskCount = 3, Changes = [] });
        _handler.SetJsonResponse($"api/servers/{serverId}/configuration/deploy",
            new ServerConfigDeployResultDto { TasksCreated = 3 });
        return Render<ServerConfigSection>(p => p.Add(x => x.ServerId, serverId));
    }

    // ── TEST 1: Default render - yaml empty, import hidden ────────────────────
    // Covers the false branches of @if (!string.IsNullOrEmpty(_configYaml)) and
    // @if (_configImportVisible)

    [Fact]
    public void Template_DefaultState_NoYamlNoImport()
    {
        var cut = RenderSection();
        Assert.Equal(1, cut.Instance.ServerId);
        // yaml card and copy button must not appear
        Assert.DoesNotContain("config-yaml-block", cut.Markup);
        Assert.DoesNotContain("ImportYamlConfiguration", cut.Markup);
    }

    // ── TEST 2: _configYaml non-empty → copy button AND yaml card (lines 9-22) ─
    // Covers @if (!string.IsNullOrEmpty(_configYaml)) → true (lines 9-13 and 16-22)

    [Fact]
    public void Template_ConfigYaml_NonEmpty_ShowsCopyButtonAndCard()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configYaml", Priv)!
            .SetValue(cut.Instance, "hostname: web01\ntype: Docker");
        cut.Render();

        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Copy);
        Assert.Contains("config-yaml-block", cut.Markup);
        Assert.Contains("hostname: web01", cut.Markup);
    }

    // ── TEST 3: _configImportVisible = true → import panel shown (lines 24-82) ─
    // Covers the outer import-panel @if block

    [Fact]
    public void Template_ConfigImportVisible_ShowsImportPanel()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configImportVisible", Priv)!.SetValue(cut.Instance, true);
        cut.Render();

        Assert.Contains("ImportYamlConfiguration", cut.Markup);
        // Validate and Preview sit in the "More actions" menu beside Deploy (STD-BTN detection 11).
        Assert.NotNull(cut.FindMenuItem("ValidateYaml"));
        Assert.NotNull(cut.FindMenuItem("Preview"));
    }

    // ── TEST 4: _configValidation not null, IsValid = true → success alert ────
    // Exercises lines 42-48 (valid branch)

    [Fact]
    public void Template_ConfigValidation_Valid_ShowsSuccessAlert()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configImportVisible", Priv)!.SetValue(cut.Instance, true);
        typeof(ServerConfigSection).GetField("_configValidation", Priv)!.SetValue(cut.Instance,
            new ServerConfigValidationResult { IsValid = true, Errors = [] });
        cut.Render();

        Assert.Contains("ConfigurationValid", cut.Markup);
    }

    // ── TEST 5: _configValidation not null, IsValid = false → error list ──────
    // Exercises lines 49-57 (invalid branch with error list @foreach)

    [Fact]
    public void Template_ConfigValidation_Invalid_ShowsErrorList()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configImportVisible", Priv)!.SetValue(cut.Instance, true);
        typeof(ServerConfigSection).GetField("_configValidation", Priv)!.SetValue(cut.Instance,
            new ServerConfigValidationResult
            {
                IsValid = false,
                Errors = ["Unknown property 'foo'", "Missing required 'name'"]
            });
        cut.Render();

        Assert.Contains("Unknown property", cut.Markup);
        Assert.Contains("Missing required", cut.Markup);
    }

    // ── TEST 6: _configPreview not null → preview card with task count ─────────
    // Exercises lines 61-80

    [Fact]
    public void Template_ConfigPreview_NotNull_ShowsPreviewCard()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configImportVisible", Priv)!.SetValue(cut.Instance, true);
        typeof(ServerConfigSection).GetField("_configPreview", Priv)!.SetValue(cut.Instance,
            new ServerConfigPreviewDto
            {
                TaskCount = 5,
                Changes =
                [
                    new ServerConfigChange { Category = "Docker", Action = "pull", Description = "Pull nginx:latest" },
                    new ServerConfigChange { Category = "Docker", Action = "create", Description = "Create web container" },
                    new ServerConfigChange { Category = "Services", Action = "update", Description = "Update nginx service" },
                    new ServerConfigChange { Category = "Services", Action = "unchanged", Description = "No change to redis" },
                    new ServerConfigChange { Category = "Docker", Action = "delete", Description = "Remove old image" }
                ]
            });
        cut.Render();

        // Task count text appears
        Assert.Contains("5", cut.Markup);
        // Changes are listed in the grid
        Assert.Contains("Docker", cut.Markup);
    }

    // ── TEST 7: GetChangeBadgeStyle for "pull" action → Success badge ─────────

    [Theory]
    [InlineData("pull", OmniTone.Success)]
    [InlineData("create", OmniTone.Success)]
    [InlineData("deploy", OmniTone.Success)]
    [InlineData("enable", OmniTone.Success)]
    [InlineData("update", OmniTone.Accent)]
    [InlineData("unchanged", OmniTone.Neutral)]
    [InlineData("delete", OmniTone.Neutral)]
    [InlineData("remove", OmniTone.Neutral)]
    public void Template_GetChangeBadgeStyle_ReturnsExpected(string action, OmniTone expected)
    {
        var method = typeof(ServerConfigSection).GetMethod(
            "GetChangeBadgeStyle",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [action])!;
        Assert.Equal(expected, result);
    }

    // ── TEST 8: Full import-panel with validation AND preview simultaneously ───
    // Ensures the @if (_configValidation is not null) + @if (_configPreview is not null)
    // both render in the same markup tree

    [Fact]
    public void Template_ImportPanel_ValidAndPreview_BothRendered()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configImportVisible", Priv)!.SetValue(cut.Instance, true);
        typeof(ServerConfigSection).GetField("_configValidation", Priv)!.SetValue(cut.Instance,
            new ServerConfigValidationResult { IsValid = true, Errors = [] });
        typeof(ServerConfigSection).GetField("_configPreview", Priv)!.SetValue(cut.Instance,
            new ServerConfigPreviewDto
            {
                TaskCount = 2,
                Changes =
                [
                    new ServerConfigChange { Category = "Server", Action = "update", Description = "Update hostname" },
                    new ServerConfigChange { Category = "Docker", Action = "unchanged", Description = "No docker changes" }
                ]
            });
        cut.Render();

        Assert.Contains("ConfigurationValid", cut.Markup);
        Assert.Contains("Server", cut.Markup);
        Assert.Contains("update", cut.Markup);
        Assert.Contains("unchanged", cut.Markup);
    }

    // ── TEST 9: _configPreview null → preview card NOT shown ─────────────────
    // Covers the @if (_configPreview is not null) false branch (line 61)

    [Fact]
    public void Template_ConfigPreview_Null_NoPreviewCard()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configImportVisible", Priv)!.SetValue(cut.Instance, true);
        // _configPreview stays null
        cut.Render();

        Assert.DoesNotContain("TasksPlanned", cut.Markup);
    }

    // ── TEST 10: _configValidation null → no validation alert ────────────────
    // Covers the @if (_configValidation is not null) false branch (line 41)

    [Fact]
    public void Template_ConfigValidation_Null_NoValidationAlert()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configImportVisible", Priv)!.SetValue(cut.Instance, true);
        // _configValidation stays null
        cut.Render();

        Assert.DoesNotContain("ConfigurationValid", cut.Markup);
    }

    // ── TEST 11: Export then show YAML card ───────────────────────────────────
    // Simulates the full export flow by setting _configYaml directly

    [Fact]
    public void Template_AfterExport_YamlCardAndCopyButton_BothPresent()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configYaml", Priv)!
            .SetValue(cut.Instance, "server:\n  name: prod-web\n  type: Docker\ntags:\n  - prod");
        cut.Render();

        Assert.Contains("config-yaml-block", cut.Markup);
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Copy);
        Assert.Contains("CurrentConfiguration", cut.Markup);
    }

    // ── TEST 12: Preview with all supported action types hits each badge style ─

    [Fact]
    public void Template_Preview_AllActionTypes_RenderedInGrid()
    {
        var cut = RenderSection();
        typeof(ServerConfigSection).GetField("_configImportVisible", Priv)!.SetValue(cut.Instance, true);
        typeof(ServerConfigSection).GetField("_configPreview", Priv)!.SetValue(cut.Instance,
            new ServerConfigPreviewDto
            {
                TaskCount = 6,
                Changes =
                [
                    new ServerConfigChange { Category = "Docker", Action = "pull", Description = "Pull image" },
                    new ServerConfigChange { Category = "Docker", Action = "create", Description = "Create ctr" },
                    new ServerConfigChange { Category = "Config", Action = "update", Description = "Update cfg" },
                    new ServerConfigChange { Category = "Config", Action = "unchanged", Description = "No change" },
                    new ServerConfigChange { Category = "Service", Action = "delete", Description = "Remove svc" },
                    new ServerConfigChange { Category = "Service", Action = "enable", Description = "Enable svc" }
                ]
            });
        cut.Render();

        Assert.Contains("6", cut.Markup);
        Assert.Contains("pull", cut.Markup);
        Assert.Contains("create", cut.Markup);
        Assert.Contains("update", cut.Markup);
        Assert.Contains("unchanged", cut.Markup);
        Assert.Contains("delete", cut.Markup);
        Assert.Contains("enable", cut.Markup);
    }
}
