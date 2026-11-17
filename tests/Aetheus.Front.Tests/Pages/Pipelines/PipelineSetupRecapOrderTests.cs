// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-40: the summary step of the setup wizard in order. What the creation generates is folded away, the
/// tags are solid badges, a value is asked in one place and has one state, the help mark sits beside its field, Enter
/// confirms a typed value and the pencil of a value to fill is the main (blue) action of its row.</summary>
public sealed class PipelineSetupRecapOrderTests : BunitContext
{
    private const string Analysis = "api/pipelines/setup/production-analysis";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupRecapOrderTests() => _handler = BunitTestHelper.RegisterServices(this);

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
            new() { Name = "PORT_FRONT", Kind = ProductionVariableKind.Port },
            new() { Name = "APPNAME", Kind = ProductionVariableKind.DefaultValue, DefaultValue = "atlas" },
            new() { Name = "DOMAIN", Kind = ProductionVariableKind.NeedsValue },
            new() { Name = "ADMIN_EMAIL", Kind = ProductionVariableKind.NeedsValue, Exists = true }
        ]
    };

    private async Task<IRenderedComponent<PipelineProductionPanel>> RenderPanelAsync()
    {
        _handler.SetJsonResponse(HttpMethod.Post, Analysis, ReadableAnalysis());
        var setup = new PipelineSetupProduction(Services.GetRequiredService<ApiClient>(), () => { });
        await setup.LoadAsync(7);
        return Render<PipelineProductionPanel>(parameters => parameters.Add(panel => panel.Setup, setup));
    }

    [Fact]
    public async Task WhatIsGenerated_IsFoldedAway_UntilTheAccordionIsOpened()
    {
        var cut = await RenderPanelAsync();

        // (a) The configuration repository and the variables of the Compose file are not shown at first.
        Assert.NotEmpty(cut.FindAll("[data-testid='setup-production-generated']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-production-config-repo']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-production-variables']"));
        // What the user has to type stays in view.
        Assert.NotEmpty(cut.FindAll("#setup-production-value-DOMAIN"));

        await cut.Find("[data-testid='setup-production-generated'] button").ClickAsync(new MouseEventArgs());

        Assert.NotEmpty(cut.FindAll("[data-testid='setup-production-config-repo']"));
        Assert.NotEmpty(cut.FindAll("[data-testid='setup-production-variables']"));
    }

    [Fact]
    public async Task TheTags_AreSolidBadges()
    {
        var cut = await RenderPanelAsync();
        await cut.Find("[data-testid='setup-production-generated'] button").ClickAsync(new MouseEventArgs());

        var badges = cut.FindAll("[data-testid^='setup-production-kind-']");
        Assert.Equal(4, badges.Count);
        Assert.All(badges, badge => Assert.Contains("solid", badge.ClassName, StringComparison.OrdinalIgnoreCase));
        Assert.All(badges, badge => Assert.DoesNotContain("outline", badge.ClassName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AVariable_HasOneState_AlreadyDefinedOrToFill_NeverBoth()
    {
        var cut = await RenderPanelAsync();
        await cut.Find("[data-testid='setup-production-generated'] button").ClickAsync(new MouseEventArgs());

        Assert.Equal("PipelineSetupProdExists", cut.Find("[data-testid='setup-production-kind-ADMIN_EMAIL']").TextContent.Trim());
        Assert.Equal("PipelineSetupProdKindNeedsValue", cut.Find("[data-testid='setup-production-kind-DOMAIN']").TextContent.Trim());
        Assert.DoesNotContain("PipelineSetupProdExists", cut.Find("[data-testid='setup-production-kind-DOMAIN']").ParentElement!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStrategyHelpMark_SitsBesideTheField_NotUnderItsTerm()
    {
        var cut = await RenderPanelAsync();

        var row = cut.Find("#setup-production-strategy").ParentElement!;
        Assert.NotNull(row.QuerySelector(".pipeline-info-tip, [data-testid='pipeline-info-tip'], .omni-tooltip"));
    }

    [Fact]
    public void AValueTheProductionPanelAsks_IsNotAskedAgainInTheFindings()
    {
        var asked = PipelineProductionPanel.ToFillNames(ReadableAnalysis());
        Assert.Contains("DOMAIN", asked);

        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.AskedElsewhere, asked)
            .Add(panel => panel.Readiness, new PipelineSetupReadinessDto
            {
                RepositoryInspected = true,
                Checks =
                [
                    new PipelineSetupReadinessCheckDto
                    {
                        Kind = PipelineSetupReadinessKind.EmptyLibraryValues, Severity = PipelineSetupReadinessSeverity.Blocking,
                        Items = ["DOMAIN", "SMTP_HOST"],
                        EmptyValues =
                        [
                            new() { OwnerId = 9, OwnerName = "prod", Key = "DOMAIN", PipelineId = 21, PipelineName = "deploy-prod" },
                            new() { OwnerId = 9, OwnerName = "prod", Key = "SMTP_HOST", PipelineId = 21, PipelineName = "deploy-prod" }
                        ]
                    }
                ]
            }));

        var keys = cut.FindAll("[data-testid='setup-readiness-empty-fill']");
        Assert.Single(keys);
        Assert.DoesNotContain("DOMAIN", cut.Find("[data-testid='setup-readiness-empty-values']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenEveryEmptyValueIsAskedElsewhere_TheFindingDisappears()
    {
        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.AskedElsewhere, new HashSet<string> { "DOMAIN" })
            .Add(panel => panel.Readiness, new PipelineSetupReadinessDto
            {
                RepositoryInspected = true,
                Checks =
                [
                    new PipelineSetupReadinessCheckDto
                    {
                        Kind = PipelineSetupReadinessKind.EmptyLibraryValues, Severity = PipelineSetupReadinessSeverity.Blocking,
                        Items = ["DOMAIN"],
                        EmptyValues = [new() { OwnerId = 9, OwnerName = "prod", Key = "DOMAIN", PipelineId = 21, PipelineName = "deploy-prod" }]
                    }
                ]
            }));

        Assert.Empty(cut.FindAll("[data-testid='setup-checks']"));
    }

    [Fact]
    public void ThePencilOfAValueToFill_IsThePrimaryAction()
    {
        var cut = Render<PipelineReadinessEmptyValues>(parameters => parameters
            .Add(component => component.FillInDialog, true)
            .Add(component => component.Rows, [new PipelineSetupReadinessEmptyValueDto
            {
                OwnerId = 9, OwnerName = "prod", Key = "DOMAIN", PipelineId = 21, PipelineName = "deploy-prod"
            }]));

        var pencil = cut.Find("[data-testid='setup-readiness-empty-fill']");
        Assert.Contains("primary", pencil.ClassName, StringComparison.OrdinalIgnoreCase);
    }
}
