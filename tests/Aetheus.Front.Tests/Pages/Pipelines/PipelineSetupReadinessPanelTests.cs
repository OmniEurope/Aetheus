// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Front.Components.Git;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Readiness findings offer relevant library and vault creation actions.</summary>
public sealed class PipelineSetupReadinessPanelTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupReadinessPanelTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineSetupReadinessDto Missing(PipelineSetupReadinessKind kind, params string[] items) => new()
    {
        RepositoryInspected = true,
        Checks = [new PipelineSetupReadinessCheckDto { Kind = kind, Severity = PipelineSetupReadinessSeverity.Blocking, Items = [.. items] }]
    };

    [Fact]
    public void AMissingLibrary_IsNamedAsSuch_NotAsAMissingEnvironment()
    {
        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.TemplateNames, ["aetheus-candidate"])
            .Add(panel => panel.Readiness, Missing(PipelineSetupReadinessKind.MissingRequiredLibraries, "aetheus-prod-host")));

        // The missing library is a chip of the single "to do" block, with what it is for as a tooltip.
        Assert.Contains("aetheus-prod-host", cut.Find("[data-testid='setup-provisioned-pending']").TextContent, StringComparison.Ordinal);
        Assert.Contains("PipelineSetupPurposeLibraryOther", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadinessMissingEnvironments", cut.Markup, StringComparison.Ordinal);
        // An unrelated server-creation remedy must not be offered.
        Assert.DoesNotContain("/servers/add-agent", cut.Markup, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("[data-testid='setup-provision']"));
    }

    [Fact]
    public void AFindingNoClickCanFix_OffersNoCreateButton()
    {
        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.TemplateNames, ["aetheus-candidate"])
            .Add(panel => panel.Readiness, Missing(PipelineSetupReadinessKind.MissingEnvironments, "qa")));

        Assert.Empty(cut.FindAll("[data-testid='setup-provision']"));
    }

    [Fact]
    public void CreatingThem_ShowsWhatWasCreated_AndAsksTheHostToCheckAgain()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/provision", new PipelineRequirementsProvisionResultDto
        {
            Libraries = [new ProvisionedRequirementDto { Id = 12, Name = "aetheus-prod-host", Keys = ["HOST", "PORT_FRONT"], KeysCopiedFrom = "Aetheus / aetheus-prod-host" }],
            Vaults = [new ProvisionedRequirementDto { Id = 13, Name = "aetheus-prod-secrets", Keys = [] }]
        });
        var rechecked = 0;

        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.TemplateNames, ["aetheus-candidate"])
            .Add(panel => panel.Readiness, Missing(PipelineSetupReadinessKind.MissingRequiredVaults, "aetheus-prod-secrets"))
            .Add(panel => panel.OnChanged, () => rechecked++));

        cut.Find("[data-testid='setup-provision'] button").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='setup-provisioned-created']").Count));
        // R2-19: nothing leaves the wizard from here.
        Assert.DoesNotContain("href=\"/variable-libraries/12\"", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/vaults/13\"", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(1, rechecked);
        Assert.Contains(_handler.Requests, request => request.Url.Contains("api/pipelines/setup/provision", StringComparison.Ordinal));
    }

    /// <summary>Each missing adapter says what the project has to write in it, not only its name;
    /// an adapter no template documents is listed bare.</summary>
    [Fact]
    public void AMissingAdapter_SaysWhatItHasToDo()
    {
        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.Readiness, Missing(PipelineSetupReadinessKind.MissingAdapterScripts,
                ".pipeline/scripts/ci.sh", ".pipeline/scripts/quality.sh", ".pipeline/scripts/custom.sh")));

        var roles = cut.FindAll("[data-testid='setup-readiness-item-role']").Select(role => role.TextContent).ToList();
        Assert.Equal(2, roles.Count);
        Assert.Contains(roles, role => role.Contains("ReadinessAdapterCi", StringComparison.Ordinal));
        Assert.Contains(roles, role => role.Contains("ReadinessAdapterQuality", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingScannerConfiguration_IsNamedExplainedAndLinkedToTheRepository()
    {
        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.Readiness, Missing(PipelineSetupReadinessKind.MissingScannerConfiguration, ".gitleaks.toml")));

        Assert.Contains("ReadinessMissingScannerConfiguration", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadinessMissingEnvironments", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("ReadinessScannerFileGitleaks", cut.Find("[data-testid='setup-readiness-item-role']").TextContent,
            StringComparison.Ordinal);
        Assert.Contains("/git-repositories?projectId=4", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>What was created stays listed as created; nothing has to be opened to go on.</summary>
    [Fact]
    public void CreatedElements_AreListedAsCreated_WithNothingToOpen()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/provision", new PipelineRequirementsProvisionResultDto
        {
            Libraries = [new ProvisionedRequirementDto { Id = 12, Name = "atlas-host", Keys = ["HOST"] }],
            Vaults = [new ProvisionedRequirementDto { Id = 13, Name = "atlas-secrets", Keys = [] }]
        });
        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.TemplateNames, ["application-ci"])
            .Add(panel => panel.ReturnUrl, "/pipelines/setup?projectId=4&options=ci&step=summary")
            .Add(panel => panel.Readiness, Missing(PipelineSetupReadinessKind.MissingRequiredVaults, "atlas-secrets")));

        cut.Find("[data-testid='setup-provision'] button").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='setup-provisioned-created']").Count));
        // R2-19: no "open each element" link and no sentence asking to open them; the pipelines can be
        // created without it.
        Assert.Empty(cut.FindAll("[data-testid='setup-provisioned-open']"));
        Assert.DoesNotContain("PipelineSetupProvisionedCheckFirst", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("returnUrl", cut.Markup, StringComparison.Ordinal);
        Assert.False(PipelineSetupCreationGate.IsBlocked(new PipelineSetupReadinessDto { RepositoryInspected = true }));
    }

    /// <summary>Coming back to the summary (a new panel) shows the same created elements.</summary>
    [Fact]
    public void CreatedElements_SurviveLeavingAndComingBackToTheSummary()
    {
        var state = Services.GetRequiredService<PipelineSetupProvisionState>();
        state.Record(4, new PipelineRequirementsProvisionResultDto
        {
            Libraries = [new ProvisionedRequirementDto { Id = 12, Name = "atlas-host", Keys = ["HOST"] }]
        });

        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.Readiness, new PipelineSetupReadinessDto { RepositoryInspected = true }));

        Assert.Single(cut.FindAll("[data-testid='setup-provisioned-created']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-provisioned-pending']"));
        Assert.Contains("PipelineSetupRecapAllReady", cut.Find("[data-testid='setup-todo-state']").TextContent, StringComparison.Ordinal);
    }

    /// <summary>R2-20: creating the elements updates the block in place. The veil lies over it, its
    /// content stays mounted underneath, and no loader replaces it.</summary>
    [Fact]
    public async Task Creating_KeepsTheBlockMountedUnderAVeil_WithoutALoader()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Post, "api/pipelines/setup/provision", async _ =>
        {
            await release.Task;
            return new PipelineRequirementsProvisionResultDto
            {
                Vaults = [new ProvisionedRequirementDto { Id = 13, Name = "atlas-secrets", Keys = [] }]
            };
        });
        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.TemplateNames, ["application-ci"])
            .Add(panel => panel.Readiness, Missing(PipelineSetupReadinessKind.MissingRequiredVaults, "atlas-secrets")));

        var click = cut.Find("[data-testid='setup-provision'] button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => Assert.Contains("omni-busy", cut.Find("[data-testid='setup-todo']").ClassList));
        // Still holding its content, and no loader took its place.
        Assert.Contains("atlas-secrets", cut.Find("[data-testid='setup-provisioned-pending']").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindComponents<AetheusLoader>());

        release.SetResult(true);
        await click;

        cut.WaitForAssertion(() => Assert.DoesNotContain("omni-busy", cut.Find("[data-testid='setup-todo']").ClassList));
        Assert.Single(cut.FindAll("[data-testid='setup-provisioned-created']"));
    }

    /// <summary>R2-20: while the host reads the verdict again, the panel keeps the verdict it shows
    /// under the veil; only a first reading, with nothing to show, is a loader.</summary>
    [Fact]
    public void RecheckingKeepsTheContentMounted_AndOnlyTheFirstReadingIsALoader()
    {
        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.Loading, true)
            .Add(panel => panel.Readiness, Missing(PipelineSetupReadinessKind.NoRepository)));

        Assert.Contains("omni-busy", cut.Find("[data-testid='setup-todo']").ClassList);
        Assert.Single(cut.FindAll("[data-testid='setup-readiness-create-repository']"));
        Assert.Empty(cut.FindComponents<AetheusLoader>());

        var first = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.Loading, true));

        Assert.Single(first.FindComponents<AetheusLoader>());
        Assert.Empty(first.FindAll("[data-testid='setup-todo']"));
    }

    /// <summary>R2-17: the single alert exists only while the creation is impossible, and an empty
    /// repository (what a new one is) is no finding.</summary>
    [Fact]
    public void TheSingleAlert_ExistsOnlyWhileCreationIsImpossible()
    {
        var blocked = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.GateCreation, true)
            .Add(panel => panel.Readiness, new PipelineSetupReadinessDto
            {
                Checks =
                [
                    new PipelineSetupReadinessCheckDto { Kind = PipelineSetupReadinessKind.NoRepository, Severity = PipelineSetupReadinessSeverity.Blocking },
                    new PipelineSetupReadinessCheckDto { Kind = PipelineSetupReadinessKind.MissingRequiredVaults, Severity = PipelineSetupReadinessSeverity.Blocking, Items = ["atlas.qa"] },
                    new PipelineSetupReadinessCheckDto { Kind = PipelineSetupReadinessKind.MissingAdapterScripts, Severity = PipelineSetupReadinessSeverity.Blocking, Items = [".pipeline/scripts/ci.sh"] }
                ]
            }));

        // Three findings, one alert: the adapter finding is a row with a tooltip, not an alert.
        Assert.Single(blocked.FindAll(".omni-alert"));
        Assert.Single(blocked.FindAll("[data-testid='setup-blocked']"));
        Assert.Contains("PipelineSetupRecapActions", blocked.Find("[data-testid='setup-todo-state']").TextContent, StringComparison.Ordinal);

        var ready = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.GateCreation, true)
            .Add(panel => panel.Readiness, Missing(PipelineSetupReadinessKind.EmptyBranch, "main")));

        Assert.Empty(ready.FindAll(".omni-alert"));
        Assert.Empty(ready.FindAll("[data-testid='setup-readiness-check']"));
        Assert.Contains("PipelineSetupRecapAllReady", ready.Find("[data-testid='setup-todo-state']").TextContent, StringComparison.Ordinal);
    }

    /// <summary>R2-13: each library or vault says what it is for in a tooltip, by the environment its
    /// name ends with.</summary>
    [Theory]
    [InlineData(false, "wordmorrow.share", "PipelineSetupPurposeLibraryShare")]
    [InlineData(false, "wordmorrow.qa", "PipelineSetupPurposeLibraryQa")]
    [InlineData(false, "wordmorrow.prod", "PipelineSetupPurposeLibraryProd")]
    [InlineData(true, "wordmorrow.qa", "PipelineSetupPurposeVaultQa")]
    [InlineData(true, "wordmorrow.prod", "PipelineSetupPurposeVaultProd")]
    [InlineData(true, "custom", "PipelineSetupPurposeVaultOther")]
    public void EachElementSaysWhatItIsFor(bool isVault, string name, string key)
    {
        Assert.Equal(key, PipelineReadinessText.PurposeKey(isVault, name));
    }

    /// <summary>The missing repository is created in the dialog over the wizard, never by leaving
    /// it, and the host is asked to check again once it exists.</summary>
    [Fact]
    public void NoRepository_OpensTheCreateDialogOverThePage_AndRechecksAfterIt()
    {
        BunitTestHelper.UseImmediateDialogs(this);
        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.OpenResult = true;
        var rechecked = 0;

        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.Readiness, Missing(PipelineSetupReadinessKind.NoRepository))
            .Add(panel => panel.OnChanged, () => rechecked++));

        Assert.DoesNotContain("create=true", cut.Markup, StringComparison.Ordinal);
        cut.Find("[data-testid='setup-readiness-create-repository']").Click();

        Assert.Equal(typeof(GitRepositoryCreateDialog), dialog.LastComponent);
        Assert.Equal(4, dialog.LastParameters![nameof(GitRepositoryCreateDialog.ProjectId)]);
        Assert.Equal(1, rechecked);
    }

    /// <summary>The pages that create an environment or add a server carry the summary as their
    /// return address.</summary>
    [Fact]
    public void EnvironmentAndServerFixes_CarryTheSummaryAsReturnAddress()
    {
        const string summary = "/pipelines/setup?projectId=4&step=summary";
        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.ReturnUrl, summary)
            .Add(panel => panel.Readiness, new PipelineSetupReadinessDto
            {
                Checks =
                [
                    new PipelineSetupReadinessCheckDto { Kind = PipelineSetupReadinessKind.MissingEnvironments, Severity = PipelineSetupReadinessSeverity.Blocking, Items = ["qa"] },
                    new PipelineSetupReadinessCheckDto { Kind = PipelineSetupReadinessKind.NoRunnerConfigured, Severity = PipelineSetupReadinessSeverity.Blocking }
                ]
            }));

        var hrefs = cut.FindAll("[data-testid='setup-readiness-check'] a").Select(link => link.GetAttribute("href")).ToList();
        Assert.Contains($"/environments/new?projectId=4&returnUrl={Uri.EscapeDataString(summary)}", hrefs);
        Assert.Contains($"/servers/add-agent?returnUrl={Uri.EscapeDataString(summary)}", hrefs);
    }

    private static PipelineSetupReadinessDto NoRepositoryAndAMissingVault() => new()
    {
        Checks =
        [
            new PipelineSetupReadinessCheckDto { Kind = PipelineSetupReadinessKind.NoRepository, Severity = PipelineSetupReadinessSeverity.Blocking },
            new PipelineSetupReadinessCheckDto { Kind = PipelineSetupReadinessKind.MissingRequiredVaults, Severity = PipelineSetupReadinessSeverity.Blocking, Items = ["atlas.qa"] }
        ]
    };

    /// <summary>R2-26: the repository line and the libraries and vaults line are two rows of one grid, each
    /// with its create button in the action cell (both Primary, the user's exception), and the list of the
    /// libraries and vaults comes below both lines.</summary>
    [Fact]
    public void TheTwoLines_ShareOneActionColumn_BothButtonsPrimary_AndTheListComesBelowBoth()
    {
        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.TemplateNames, ["application-ci"])
            .Add(panel => panel.Readiness, NoRepositoryAndAMissingVault()));

        var repository = cut.Find("[data-testid='setup-todo-repository']");
        var elements = cut.Find("[data-testid='setup-todo-elements']");
        var list = cut.Find("[data-testid='setup-todo-elements-list']");
        var repositoryButton = repository.QuerySelector(".pipeline-setup-todo-action button");
        var provisionButton = elements.QuerySelector(".pipeline-setup-todo-action button");

        Assert.NotNull(repositoryButton);
        Assert.NotNull(provisionButton);
        Assert.Contains("omni-button--primary", repositoryButton.ClassList);
        Assert.Contains("omni-button--primary", provisionButton.ClassList);
        // One grid holds the two lines and the list: same parent, in this order.
        var grid = repository.ParentElement!;
        Assert.Contains("pipeline-setup-todo", grid.ClassList);
        Assert.Same(grid, elements.ParentElement);
        Assert.Same(grid, list.ParentElement);
        Assert.Equal(["setup-todo-repository", "setup-todo-elements", "setup-todo-elements-list"],
            grid.Children.Select(child => child.GetAttribute("data-testid")));
        Assert.Contains("atlas.qa", list.TextContent, StringComparison.Ordinal);
    }

    /// <summary>R2-26: once the repository exists the line keeps its structure (its action cell stays, empty),
    /// so nothing moves; the libraries and vaults line is unchanged.</summary>
    [Fact]
    public void TheRepositoryLine_KeepsItsStructure_WhenItsButtonIsGone()
    {
        var before = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.TemplateNames, ["application-ci"])
            .Add(panel => panel.Readiness, NoRepositoryAndAMissingVault()));
        var after = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.TemplateNames, ["application-ci"])
            .Add(panel => panel.Readiness, Missing(PipelineSetupReadinessKind.MissingRequiredVaults, "atlas.qa")));

        var rowBefore = before.Find("[data-testid='setup-todo-repository']");
        var rowAfter = after.Find("[data-testid='setup-todo-repository']");
        Assert.Equal(rowBefore.Children.Select(child => child.ClassList.Contains("pipeline-setup-todo-action")),
            rowAfter.Children.Select(child => child.ClassList.Contains("pipeline-setup-todo-action")));
        Assert.Empty(rowAfter.QuerySelectorAll("button"));
        Assert.NotNull(rowAfter.QuerySelector(".pipeline-setup-todo-action"));
        Assert.Single(after.FindAll("[data-testid='setup-provision'] button"));
    }

    /// <summary>R2-27: each empty variable or secret is a link to its own library or vault, back to the
    /// summary; one whose holder is not known stays plain text.</summary>
    [Fact]
    public void EachEmptyVariableAndSecret_IsALinkToItsOwnLibraryOrVault()
    {
        const string summary = "/pipelines/setup?projectId=4&step=summary";
        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.ReturnUrl, summary)
            .Add(panel => panel.Readiness, new PipelineSetupReadinessDto
            {
                RepositoryInspected = true,
                Checks =
                [
                    new PipelineSetupReadinessCheckDto
                    {
                        Kind = PipelineSetupReadinessKind.EmptyLibraryValues, Severity = PipelineSetupReadinessSeverity.Blocking,
                        Items = ["API_HOST", "API_PORT"], ItemIds = new Dictionary<string, int> { ["API_HOST"] = 12 }
                    },
                    new PipelineSetupReadinessCheckDto
                    {
                        Kind = PipelineSetupReadinessKind.EmptyVaultSecrets, Severity = PipelineSetupReadinessSeverity.Blocking,
                        Items = ["DB_PASSWORD"], ItemIds = new Dictionary<string, int> { ["DB_PASSWORD"] = 9 }
                    }
                ]
            }));

        var links = cut.FindAll("[data-testid='setup-readiness-item-link']");
        Assert.Equal(2, links.Count);
        Assert.Equal($"/variable-libraries/12?returnUrl={Uri.EscapeDataString(summary)}", links[0].GetAttribute("href"));
        Assert.Equal("API_HOST", links[0].TextContent);
        Assert.Equal($"/vaults/9?returnUrl={Uri.EscapeDataString(summary)}", links[1].GetAttribute("href"));
        Assert.Equal("DB_PASSWORD", links[1].TextContent);
        // API_PORT is listed, but nothing says where it lives: no link.
        Assert.Contains("API_PORT", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(links, link => link.TextContent == "API_PORT");
    }

    /// <summary>An external repository is never announced as missing files.</summary>
    [Fact]
    public void ExternalRepositoryNotInspected_SaysTheFilesCannotBeChecked()
    {
        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.Readiness, new PipelineSetupReadinessDto
            {
                Checks = [new PipelineSetupReadinessCheckDto { Kind = PipelineSetupReadinessKind.ExternalRepositoryNotInspected, Severity = PipelineSetupReadinessSeverity.Warning, Items = [".pipeline/scripts/ci.sh"] }]
            }));

        Assert.Contains("ReadinessExternalRepositoryNotInspected", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadinessMissingAdapterScripts", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadinessNoRepository", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("/projects/4/external-repo", cut.Markup, StringComparison.Ordinal);
    }
}
