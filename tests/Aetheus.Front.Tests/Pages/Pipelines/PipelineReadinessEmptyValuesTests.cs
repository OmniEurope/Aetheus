// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-10: the "not configured" box lists the empty values as a table of library or vault, key and
/// pipeline, each cell a link to where it is fixed, one row per key and pipeline.</summary>
public sealed class PipelineReadinessEmptyValuesTests : BunitContext
{
    public PipelineReadinessEmptyValuesTests() => BunitTestHelper.RegisterServices(this);

    private const string Summary = "/projects/4/pipelines";

    private IRenderedComponent<PipelineReadinessChecks> RenderChecks(params PipelineSetupReadinessCheckDto[] checks) =>
        Render<PipelineReadinessChecks>(parameters => parameters
            .Add(component => component.ProjectId, 4)
            .Add(component => component.ReturnUrl, Summary)
            .Add(component => component.Checks, checks));

    private static PipelineSetupReadinessCheckDto LibraryCheck() => new()
    {
        Kind = PipelineSetupReadinessKind.EmptyLibraryValues,
        Severity = PipelineSetupReadinessSeverity.Blocking,
        Items = ["HOST_PREFIX"],
        ItemIds = new Dictionary<string, int> { ["HOST_PREFIX"] = 12 },
        EmptyValues =
        [
            new() { OwnerId = 12, OwnerName = "aetheus.prod", Key = "HOST_PREFIX", PipelineId = 22, PipelineName = "release-strict" },
            new() { OwnerId = 12, OwnerName = "aetheus.prod", Key = "HOST_PREFIX", PipelineId = 23, PipelineName = "revert-strict" }
        ]
    };

    [Fact]
    public void ALibraryKeyTwoPipelinesRead_IsTwoRowsOfThreeLinks()
    {
        var cut = RenderChecks(LibraryCheck());

        Assert.Equal(2, cut.FindAll("[data-testid='setup-readiness-empty-pipeline']").Count);
        var owners = cut.FindAll("[data-testid='setup-readiness-empty-owner']");
        var keys = cut.FindAll("[data-testid='setup-readiness-empty-key']");
        var pipelines = cut.FindAll("[data-testid='setup-readiness-empty-pipeline']");
        var returnUrl = $"returnUrl={Uri.EscapeDataString(Summary)}";
        Assert.All(owners, owner => Assert.Equal($"/variable-libraries/12?{returnUrl}", owner.GetAttribute("href")));
        Assert.All(owners, owner => Assert.Equal("aetheus.prod", owner.TextContent));
        Assert.All(keys, key => Assert.Equal($"/variable-libraries/12?key=HOST_PREFIX&{returnUrl}", key.GetAttribute("href")));
        Assert.All(keys, key => Assert.Equal("HOST_PREFIX", key.TextContent));
        Assert.Equal([$"/pipelines/22?{returnUrl}", $"/pipelines/23?{returnUrl}"], pipelines.Select(link => link.GetAttribute("href")));
        Assert.Equal(["release-strict", "revert-strict"], pipelines.Select(link => link.TextContent));
        // The table replaces the bare list of names and the generic link to all the libraries.
        Assert.Empty(cut.FindAll("[data-testid='setup-readiness-item-link']"));
        Assert.DoesNotContain("href=\"/projects/4/libraries", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>R-52: the content of an alert (or of the badge row of a finding) starts after the finding's icon, so a
    /// table drawn inside it carried a useless offset on the left. It follows the finding as a block of its own.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTableOfEmptyValues_FollowsTheFindingOverTheFullWidth_NotInsideItsTextColumn(bool blockingAsAlert)
    {
        var cut = Render<PipelineReadinessChecks>(parameters => parameters
            .Add(component => component.ProjectId, 4)
            .Add(component => component.ReturnUrl, Summary)
            .Add(component => component.BlockingAsAlert, blockingAsAlert)
            .Add(component => component.Checks, new[] { LibraryCheck() }));

        var finding = cut.Find("[data-testid='setup-readiness-check']");
        Assert.Empty(finding.QuerySelectorAll("[data-testid='setup-readiness-empty-values']"));
        var table = Assert.Single(cut.FindAll("[data-testid='setup-readiness-empty-values']"));
        Assert.Same(finding.NextElementSibling, table);
        Assert.Contains("pipeline-readiness-empty-values", table.ClassName ?? string.Empty, StringComparison.Ordinal);
        // The finding keeps its title and its explanation, only the table moved out.
        Assert.Contains("ReadinessEmptyLibraryValues", finding.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheColumnsAreLibraryParameterAndPipeline()
    {
        var cut = RenderChecks(LibraryCheck());

        var headers = cut.FindAll("th").Select(header => header.TextContent.Trim()).Where(text => text.Length > 0).ToList();
        Assert.Contains(headers, header => header.StartsWith("VariableLibrary", StringComparison.Ordinal));
        Assert.Contains(headers, header => header.StartsWith("Parameter", StringComparison.Ordinal));
        Assert.Contains(headers, header => header.StartsWith("Pipeline", StringComparison.Ordinal));
    }

    [Fact]
    public void VaultSecrets_UseTheSameTableAndLinkToTheVault()
    {
        var cut = RenderChecks(new PipelineSetupReadinessCheckDto
        {
            Kind = PipelineSetupReadinessKind.EmptyVaultSecrets,
            Severity = PipelineSetupReadinessSeverity.Blocking,
            Items = ["DB_PASSWORD"],
            EmptyValues = [new() { OwnerId = 9, OwnerName = "aetheus-deploy", Key = "DB_PASSWORD", PipelineId = 21, PipelineName = "deploy-prod" }]
        });

        var returnUrl = $"returnUrl={Uri.EscapeDataString(Summary)}";
        Assert.Equal($"/vaults/9?{returnUrl}", cut.Find("[data-testid='setup-readiness-empty-owner']").GetAttribute("href"));
        Assert.Equal($"/vaults/9?key=DB_PASSWORD&{returnUrl}", cut.Find("[data-testid='setup-readiness-empty-key']").GetAttribute("href"));
        Assert.Equal($"/pipelines/21?{returnUrl}", cut.Find("[data-testid='setup-readiness-empty-pipeline']").GetAttribute("href"));
        Assert.Contains("ReadinessColumnSecret", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowWhoseOwnerOrPipelineIsUnknown_ShowsTheTextWithoutALink()
    {
        var cut = RenderChecks(new PipelineSetupReadinessCheckDto
        {
            Kind = PipelineSetupReadinessKind.EmptyLibraryValues,
            Severity = PipelineSetupReadinessSeverity.Blocking,
            Items = ["API_PORT"],
            EmptyValues = [new() { Key = "API_PORT", PipelineName = "application-ci-template" }]
        });

        Assert.Contains("API_PORT", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("application-ci-template", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='setup-readiness-empty-owner']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-readiness-empty-key']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-readiness-empty-pipeline']"));
    }

    [Fact]
    public void AFindingWithoutRows_KeepsTheListOfNamesAndTheGenericLink()
    {
        var cut = RenderChecks(new PipelineSetupReadinessCheckDto
        {
            Kind = PipelineSetupReadinessKind.EmptyLibraryValues,
            Severity = PipelineSetupReadinessSeverity.Blocking,
            Items = ["API_HOST"],
            ItemIds = new Dictionary<string, int> { ["API_HOST"] = 12 }
        });

        Assert.Single(cut.FindAll("[data-testid='setup-readiness-item-link']"));
        Assert.Contains("href=\"/projects/4/libraries", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='setup-readiness-empty-values']"));
    }
}
