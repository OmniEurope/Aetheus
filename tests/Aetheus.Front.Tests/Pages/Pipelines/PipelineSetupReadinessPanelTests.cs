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

        Assert.Contains("ReadinessMissingRequiredLibraries", cut.Markup, StringComparison.Ordinal);
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

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-provisioned']")));
        Assert.Contains("href=\"/variable-libraries/12\"", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("href=\"/vaults/13\"", cut.Markup, StringComparison.Ordinal);
        // The vault had no readable source: the panel says it starts empty rather than implying keys.
        Assert.Contains("PipelineSetupProvisionedEmpty", cut.Markup, StringComparison.Ordinal);
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

    /// <summary>What was created stays listed as created, each element opens with the summary as
    /// its return address, and only once opened does it read as checked.</summary>
    [Fact]
    public void ACreatedElement_IsCheckedOnlyOnceTheUserOpenedIt()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/provision", new PipelineRequirementsProvisionResultDto
        {
            Libraries = [new ProvisionedRequirementDto { Id = 12, Name = "atlas-host", Keys = ["HOST"] }],
            Vaults = [new ProvisionedRequirementDto { Id = 13, Name = "atlas-secrets", Keys = [] }]
        });
        const string summary = "/pipelines/setup?projectId=4&options=ci&step=summary";
        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.TemplateNames, ["application-ci"])
            .Add(panel => panel.ReturnUrl, summary)
            .Add(panel => panel.Readiness, Missing(PipelineSetupReadinessKind.MissingRequiredVaults, "atlas-secrets")));

        cut.Find("[data-testid='setup-provision'] button").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='setup-provisioned-created']").Count));
        Assert.Empty(cut.FindAll("[data-testid='setup-provisioned-checked']"));
        Assert.Single(cut.FindAll("[data-testid='setup-provisioned-pending']"));
        var state = Services.GetRequiredService<PipelineSetupProvisionState>();
        Assert.False(state.AllChecked(4));
        var links = cut.FindAll("[data-testid='setup-provisioned-open']");
        Assert.Equal($"/variable-libraries/12?returnUrl={Uri.EscapeDataString(summary)}", links[0].GetAttribute("href"));
        Assert.Equal($"/vaults/13?returnUrl={Uri.EscapeDataString(summary)}", links[1].GetAttribute("href"));

        links[0].Click();

        Assert.Single(cut.FindAll("[data-testid='setup-provisioned-checked']"));
        Assert.False(state.AllChecked(4));
        cut.FindAll("[data-testid='setup-provisioned-open']")[1].Click();
        Assert.Equal(2, cut.FindAll("[data-testid='setup-provisioned-checked']").Count);
        Assert.Empty(cut.FindAll("[data-testid='setup-provisioned-pending']"));
        Assert.True(state.AllChecked(4));
    }

    /// <summary>Coming back to the summary (a new panel) shows the same created elements with what
    /// was already checked.</summary>
    [Fact]
    public void CreatedElements_SurviveLeavingAndComingBackToTheSummary()
    {
        var state = Services.GetRequiredService<PipelineSetupProvisionState>();
        state.Record(4, new PipelineRequirementsProvisionResultDto
        {
            Libraries = [new ProvisionedRequirementDto { Id = 12, Name = "atlas-host", Keys = ["HOST"] }]
        });
        state.MarkChecked(4, ProvisionedKind.Library, 12);

        var cut = Render<PipelineSetupReadinessPanel>(parameters => parameters
            .Add(panel => panel.ProjectId, 4)
            .Add(panel => panel.Readiness, new PipelineSetupReadinessDto { RepositoryInspected = true }));

        Assert.Single(cut.FindAll("[data-testid='setup-provisioned-checked']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-provisioned-pending']"));
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
