// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// PLAN-003 lot 30 / D22: the readiness panel offers to create the libraries and vaults the templates
/// require. It also fixes the titles those findings had: they fell through to "missing environments"
/// and linked to "add a server".
/// </summary>
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
        // The wrong remedy it used to point at.
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
            .Add(panel => panel.OnProvisioned, () => rechecked++));

        cut.Find("[data-testid='setup-provision'] button").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-provisioned']")));
        Assert.Contains("href=\"/variable-libraries/12\"", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("href=\"/vaults/13\"", cut.Markup, StringComparison.Ordinal);
        // The vault had no readable source: the panel says it starts empty rather than implying keys.
        Assert.Contains("PipelineSetupProvisionedEmpty", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(1, rechecked);
        Assert.Contains(_handler.Requests, request => request.Url.Contains("api/pipelines/setup/provision", StringComparison.Ordinal));
    }

    /// <summary>Recette R2-121: each missing adapter says what the project has to write in it, not only
    /// its name; an adapter no template documents is listed bare.</summary>
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
}
