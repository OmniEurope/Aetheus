// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recettes R-53 and R-54: the values to fill in are one compact row per variable (name with its help mark,
/// field, small state badge), and a value typed without Enter is kept and used by the creation.</summary>
public sealed class PipelineProductionValuesTests : BunitContext
{
    private const string Analysis = "api/pipelines/setup/production-analysis";
    private const string Apply = "api/pipelines/setup/production-apply";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineProductionValuesTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineProductionAnalysisDto ReadableAnalysis() => new()
    {
        Readable = true,
        Branch = "develop",
        ComposeFiles = ["deploy/compose/remote.compose.yml"],
        ComposeFile = "deploy/compose/remote.compose.yml",
        Servers = [new ProductionServerDto { Id = 1, Name = "vps" }],
        DefaultServer = "vps",
        Variables =
        [
            new() { Name = "DOMAIN", Kind = ProductionVariableKind.NeedsValue },
            new() { Name = "SMTP_PASSWORD", Kind = ProductionVariableKind.NeedsValue, Secret = true },
            new() { Name = "APPNAME", Kind = ProductionVariableKind.DefaultValue, DefaultValue = "atlas" }
        ]
    };

    private async Task<(IRenderedComponent<PipelineProductionPanel> Cut, PipelineSetupProduction Setup)> RenderPanelAsync()
    {
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, ReadableAnalysis());
        var setup = new PipelineSetupProduction(Services.GetRequiredService<ApiClient>(), () => { });
        await setup.LoadAsync(7);
        return (Render<PipelineProductionPanel>(parameters => parameters.Add(panel => panel.Setup, setup)), setup);
    }

    [Fact]
    public async Task AValueTypedWithoutEnter_IsKept_ShownAsTyped_AndSentByTheCreation()
    {
        var (cut, setup) = await RenderPanelAsync();
        _handler.SetJsonResponse(HttpMethod.Post, Apply, new PipelineProductionApplyResultDto());

        await cut.Find("#setup-production-value-DOMAIN").InputAsync(new ChangeEventArgs { Value = "atlas.example.org" });

        Assert.Equal("PipelineSetupProdTyped", cut.Find("[data-testid='setup-production-kind-DOMAIN']").TextContent.Trim());
        await setup.ApplyAsync(7);
        Assert.Contains("atlas.example.org", _handler.LastRequestBody);
    }

    [Fact]
    public async Task AValueEmptiedAgain_ShowsToFillInAgain()
    {
        var (cut, _) = await RenderPanelAsync();
        await cut.Find("#setup-production-value-DOMAIN").InputAsync(new ChangeEventArgs { Value = "x" });
        await cut.Find("#setup-production-value-DOMAIN").InputAsync(new ChangeEventArgs { Value = "" });

        Assert.Equal("PipelineSetupProdKindNeedsValue", cut.Find("[data-testid='setup-production-kind-DOMAIN']").TextContent.Trim());
    }

    [Fact]
    public async Task EachValueToFill_IsOneRow_WithItsNameFieldBadgeAndHelpMark()
    {
        var (cut, _) = await RenderPanelAsync();

        var rows = cut.FindAll("[data-testid='setup-production-to-fill'] [data-testid='setup-production-row']");
        Assert.Equal(2, rows.Count);
        foreach (var (row, name) in rows.Zip(new[] { "DOMAIN", "SMTP_PASSWORD" }))
        {
            Assert.NotNull(row.QuerySelector($"#setup-production-value-{name}"));
            Assert.NotNull(row.QuerySelector($"[data-testid='setup-production-kind-{name}']"));
            Assert.Contains(name, row.QuerySelector("label")!.TextContent, StringComparison.Ordinal);
            // The help sits behind the "?" of the label, and the field no longer repeats it as a placeholder.
            Assert.NotNull(row.QuerySelector(".omni-form-field__help-mark"));
            Assert.Null(row.QuerySelector($"#setup-production-value-{name}")!.GetAttribute("placeholder"));
        }
    }

    [Fact]
    public async Task WhatIsGenerated_IsOneCompactList_NameBadgeAndValueOnOneRow()
    {
        var (cut, _) = await RenderPanelAsync();
        await cut.Find("[data-testid='setup-production-generated'] button").ClickAsync(new MouseEventArgs());

        var row = cut.Find("[data-testid='setup-production-variables'] [data-testid='setup-production-generated-row']");
        Assert.Contains("APPNAME", row.TextContent, StringComparison.Ordinal);
        Assert.NotNull(row.QuerySelector("[data-testid='setup-production-kind-APPNAME']"));
        Assert.Contains("atlas", row.TextContent, StringComparison.Ordinal);
    }
}
