// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-56: "Attention, il manque des variables" is a table, like the empty values (R-10): each undefined variable
/// is a link to its entry (or to the creation of its holder), beside the library or the vault it must be created in,
/// by name, a link that opens it. A secret goes to a vault, a plain value to a library.</summary>
public sealed class PipelineReadinessUndefinedVariablesTests : BunitContext
{
    public PipelineReadinessUndefinedVariablesTests() => BunitTestHelper.RegisterServices(this);

    private const string Summary = "/projects/4/overview";
    private static readonly string ReturnUrl = $"returnUrl={Uri.EscapeDataString(Summary)}";

    private IRenderedComponent<PipelineReadinessChecks> RenderChecks(params PipelineSetupReadinessUndefinedVariableDto[] rows) =>
        RenderChecksOn(Summary, rows);

    private IRenderedComponent<PipelineReadinessChecks> RenderChecksOn(string returnUrl, params PipelineSetupReadinessUndefinedVariableDto[] rows) =>
        Render<PipelineReadinessChecks>(parameters => parameters
            .Add(component => component.ProjectId, 4)
            .Add(component => component.ReturnUrl, returnUrl)
            .Add(component => component.Checks, new[]
            {
                new PipelineSetupReadinessCheckDto
                {
                    Kind = PipelineSetupReadinessKind.UndefinedVariables,
                    Severity = PipelineSetupReadinessSeverity.Blocking,
                    Items = [.. rows.Select(row => row.Name)],
                    UndefinedVariables = [.. rows]
                }
            }));

    private static PipelineSetupReadinessUndefinedVariableDto Library(string name, int? id, string owner) => new()
    {
        Name = name,
        Container = PipelineSetupReadinessContainerKind.Library,
        OwnerId = id,
        OwnerName = owner
    };

    private static PipelineSetupReadinessUndefinedVariableDto Vault(string name, int? id, string owner) => new()
    {
        Name = name,
        Container = PipelineSetupReadinessContainerKind.Vault,
        OwnerId = id,
        OwnerName = owner
    };

    [Fact]
    public void ARowOfAnExistingVaultAndLibrary_HasTwoLinks_ToTheEntryAndToTheHolder()
    {
        var cut = RenderChecks(Vault("DB_PASSWORD", 9, "w7.vault"), Library("API_HOST", 12, "w7.share"));

        var names = cut.FindAll("[data-testid='setup-readiness-undefined-name']");
        var owners = cut.FindAll("[data-testid='setup-readiness-undefined-owner']");
        Assert.Equal(["API_HOST", "DB_PASSWORD"], names.Select(link => link.TextContent));
        Assert.Equal(
            [$"/variable-libraries/12?key=API_HOST&{ReturnUrl}", $"/vaults/9?key=DB_PASSWORD&{ReturnUrl}"],
            names.Select(link => link.GetAttribute("href")));
        Assert.Equal(["w7.share", "w7.vault"], owners.Select(link => link.TextContent));
        Assert.Equal(
            [$"/variable-libraries/12?{ReturnUrl}", $"/vaults/9?{ReturnUrl}"],
            owners.Select(link => link.GetAttribute("href")));
        // The table replaces the bare list and the one generic link to the libraries page.
        Assert.Empty(cut.FindAll("[data-testid='setup-readiness-item-link']"));
        Assert.Empty(cut.FindAll("ul.pipeline-setup-readiness-items"));
        Assert.DoesNotContain("href=\"/projects/4/libraries", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTable_FollowsTheFinding_AsABlockOfItsOwn_WithTheVaultOrLibraryLabel()
    {
        var cut = RenderChecks(Vault("DB_PASSWORD", 9, "w7.vault"), Library("API_HOST", 12, "w7.share"));

        var finding = cut.Find("[data-testid='setup-readiness-check']");
        var table = Assert.Single(cut.FindAll("[data-testid='setup-readiness-undefined-variables']"));
        Assert.Same(finding.NextElementSibling, table);
        Assert.Contains("ReadinessUndefinedVariables", finding.TextContent, StringComparison.Ordinal);
        var kinds = cut.FindAll("[data-testid='setup-readiness-undefined-kind']").Select(kind => kind.TextContent.Trim());
        Assert.Equal(["VariableLibrary", "Vault"], kinds);
    }

    [Fact]
    public void ADeclaredHolderNobodyCreated_IsNamedWithALinkToItsCreation()
    {
        var cut = RenderChecks(Vault("JWT_KEY", null, "w7.vault"), Library("API_HOST", null, "w7.share"));

        // R-24: the creation carries the key, so the value is asked for right after the holder is created.
        var owners = cut.FindAll("[data-testid='setup-readiness-undefined-owner']");
        Assert.Equal(
            [$"/variable-libraries/new?projectId=4&name=w7.share&key=API_HOST&{ReturnUrl}", $"/vaults/new?projectId=4&name=w7.vault&key=JWT_KEY&{ReturnUrl}"],
            owners.Select(link => link.GetAttribute("href")));
        // No entry to open yet: the name goes to the same creation.
        Assert.Equal(
            [$"/variable-libraries/new?projectId=4&name=w7.share&key=API_HOST&{ReturnUrl}", $"/vaults/new?projectId=4&name=w7.vault&key=JWT_KEY&{ReturnUrl}"],
            cut.FindAll("[data-testid='setup-readiness-undefined-name']").Select(link => link.GetAttribute("href")));
        Assert.Equal(2, cut.FindAll("[data-testid='setup-readiness-undefined-to-create']").Count);
    }

    /// <summary>R-24: the pipeline lists no holder and none holds the key: still a destination for every row, the creation of
    /// a vault (a secret) or a library for the project with the key carried, and the hint to declare it.</summary>
    [Fact]
    public void WhenThePipelineDeclaresNoHolderOfTheKind_EveryRowStillHasALinkToCreateOneForTheKey()
    {
        var cut = RenderChecks(Vault("DB_PASSWORD", null, string.Empty), Library("API_HOST", null, string.Empty));

        Assert.Empty(cut.FindAll("[data-testid='setup-readiness-undefined-owner']"));
        var names = cut.FindAll("[data-testid='setup-readiness-undefined-name']");
        Assert.Equal(
            [$"/variable-libraries/new?projectId=4&key=API_HOST&{ReturnUrl}", $"/vaults/new?projectId=4&key=DB_PASSWORD&{ReturnUrl}"],
            names.Select(link => link.GetAttribute("href")));
        Assert.Equal(
            names.Select(link => link.GetAttribute("href")),
            cut.FindAll("[data-testid='setup-readiness-undefined-create']").Select(link => link.GetAttribute("href")));
        var text = cut.Find("[data-testid='setup-readiness-undefined-variables']").TextContent;
        Assert.Contains("ReadinessCreateVaultForKey", text, StringComparison.Ordinal);
        Assert.Contains("ReadinessCreateLibraryForKey", text, StringComparison.Ordinal);
        Assert.Contains("ReadinessNoVaultDeclared", text, StringComparison.Ordinal);
        Assert.Contains("ReadinessNoLibraryDeclared", text, StringComparison.Ordinal);
    }

    /// <summary>R-25: the key exists in a vault of the project that this pipeline does not declare. The row says so, links to
    /// that vault (on the key) and to the pipeline to declare it, and never asks to create the key or a vault again.</summary>
    [Fact]
    public void AKeyThatExistsInAVaultTheOpenedPipelineDoesNotDeclare_IsSaidSo_WithLinksToTheVaultAndThePipeline()
    {
        // R-24: whoever may write the pipeline gets the one-click declaration instead (PipelineReadinessDeclareHolderTests);
        // a reader keeps the link that opens it.
        Services.GetRequiredService<Aetheus.Front.Components.Shared.PermissionService>().SetPermissions([], isAdmin: false);
        var row = Vault("JWT_KEY", null, string.Empty) with
        {
            ExistingHolderId = 9,
            ExistingHolderName = "w1.prod",
            ExistingHolderKind = PipelineSetupReadinessContainerKind.Vault,
            PipelineId = 176,
            PipelineName = "w1-release-fast"
        };

        var cut = RenderChecks(row);

        Assert.Equal(
            $"/vaults/9?key=JWT_KEY&{ReturnUrl}", cut.Find("[data-testid='setup-readiness-undefined-name']").GetAttribute("href"));
        var vault = cut.Find("[data-testid='setup-readiness-undefined-owner']");
        Assert.Equal(("w1.prod", $"/vaults/9?{ReturnUrl}"), (vault.TextContent, vault.GetAttribute("href")));
        Assert.Equal($"/pipelines/176?{ReturnUrl}", cut.Find("[data-testid='setup-readiness-undefined-declare']").GetAttribute("href"));
        var text = cut.Find("[data-testid='setup-readiness-undefined-variables']").TextContent;
        Assert.Contains("ReadinessExistsInVault", text, StringComparison.Ordinal);
        Assert.Contains("ReadinessNotDeclaredVault", text, StringComparison.Ordinal);
        Assert.Contains("ReadinessDeclareUnderVaults", text, StringComparison.Ordinal);
        // Nothing asks to create it again.
        Assert.Empty(cut.FindAll("[data-testid='setup-readiness-undefined-create']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-readiness-undefined-none']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-readiness-undefined-to-create']"));
    }

    /// <summary>R-25: a library is feminine in French ("ne la déclare pas"), so its sentence is its own key, not the vault's.</summary>
    [Fact]
    public void AKeyThatExistsInAnUndeclaredLibrary_IsSaidWithTheLibrarySentence()
    {
        var row = Library("APPLICATION_CANDIDATE_VERSION", 9, "toto-observability") with
        {
            ExistingHolderId = 7,
            ExistingHolderName = "toto-conformance",
            ExistingHolderKind = PipelineSetupReadinessContainerKind.Library,
            PipelineId = 49,
            PipelineName = "toto-deploy-accept-adapter"
        };

        var text = RenderChecks(row).Find("[data-testid='setup-readiness-undefined-not-declared']").TextContent;

        Assert.Equal("ReadinessNotDeclaredLibrary", text);
    }

    /// <summary>R-25: on the pipeline's own page (its readiness block returns to it), the link to open that pipeline would
    /// only reload the page: it is not drawn, the sentence and the holder's link stay.</summary>
    [Fact]
    public void OnThePipelinesOwnPage_TheLinkToOpenThatPipeline_IsNotDrawn()
    {
        var row = Vault("JWT_KEY", null, string.Empty) with
        {
            ExistingHolderId = 9,
            ExistingHolderName = "w1.prod",
            ExistingHolderKind = PipelineSetupReadinessContainerKind.Vault,
            PipelineId = 176,
            PipelineName = "w1-release-fast"
        };

        var cut = RenderChecksOn("/pipelines/176", row);

        Assert.Empty(cut.FindAll("[data-testid='setup-readiness-undefined-declare']"));
        Assert.Equal("/vaults/9?returnUrl=%2Fpipelines%2F176",
            cut.Find("[data-testid='setup-readiness-undefined-owner']").GetAttribute("href"));
        Assert.Single(cut.FindAll("[data-testid='setup-readiness-undefined-not-declared']"));
    }
}
