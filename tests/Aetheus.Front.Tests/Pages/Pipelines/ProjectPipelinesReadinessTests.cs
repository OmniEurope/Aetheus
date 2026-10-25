// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Readiness shows missing configuration and resolution links; return destinations must
/// remain local paths.</summary>
public sealed class ProjectPipelinesReadinessTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectPipelinesReadinessTests() => _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);

    private static PipelineSetupReadinessDto Verdict(params PipelineSetupReadinessCheckDto[] checks) => new()
    {
        RepositoryInspected = true,
        InspectedBranch = "main",
        Checks = [.. checks]
    };

    private static PipelineSetupReadinessCheckDto Blocking(PipelineSetupReadinessKind kind, params string[] items) => new()
    {
        Kind = kind,
        Severity = PipelineSetupReadinessSeverity.Blocking,
        Items = [.. items]
    };

    [Fact]
    public void ProjectPage_NamesWhatIsMissing_WithItsFix_AndComesBackToThePipelines()
    {
        _handler.SetJsonResponse("api/pipelines/setup/project-readiness?projectId=5", Verdict(
            Blocking(PipelineSetupReadinessKind.MissingAdapterScripts, ".pipeline/scripts/ci.sh"),
            Blocking(PipelineSetupReadinessKind.MissingScannerConfiguration, ".gitleaks.toml"),
            Blocking(PipelineSetupReadinessKind.MissingEnvironments, "production")));

        var cut = Render<ProjectPipelinesReadiness>(parameters => parameters.Add(panel => panel.ProjectId, 5));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='setup-readiness-check']").Count));
        Assert.Contains("ProjectPipelinesNotReady", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(".pipeline/scripts/ci.sh", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(".gitleaks.toml", cut.Markup, StringComparison.Ordinal);
        var hrefs = cut.FindAll("a").Select(link => link.GetAttribute("href")).ToList();
        Assert.Contains("/git-repositories?projectId=5", hrefs);
        Assert.Contains($"/environments/new?projectId=5&returnUrl={Uri.EscapeDataString("/projects/5/pipelines")}", hrefs);
    }

    /// <summary>R-15: the branch the files were checked on is an information tooltip next to the heading, not a
    /// sentence of the page; R-10 to R-13: the findings about the default commands read like the others.</summary>
    [Fact]
    public void ProjectPage_CheckedBranchIsAnInfoTooltip_AndTheDefaultCommandFindingsAreListed()
    {
        _handler.SetJsonResponse("api/pipelines/setup/project-readiness?projectId=5", Verdict(
            Blocking(PipelineSetupReadinessKind.NoDotNetSolution, "APPLICATION_CI_COMMANDS"),
            Blocking(PipelineSetupReadinessKind.NoDotNetTestProject, "APPLICATION_QA_COMMANDS"),
            Blocking(PipelineSetupReadinessKind.NoComposeFile, "compose.yaml", "compose.yml"),
            Blocking(PipelineSetupReadinessKind.NoDockerRunner, "APPLICATION_DEPLOY_PROD_COMMANDS")));

        var cut = Render<ProjectPipelinesReadiness>(parameters => parameters.Add(panel => panel.ProjectId, 5));

        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("[data-testid='setup-readiness-check']").Count));
        foreach (var title in new[] { "ReadinessNoDotNetSolution", "ReadinessNoDotNetTestProject", "ReadinessNoComposeFile", "ReadinessNoDockerRunner" })
            Assert.Contains(title, cut.Markup, StringComparison.Ordinal);
        Assert.Contains("compose.yaml", cut.Markup, StringComparison.Ordinal);

        // The branch is carried by the heading's info tip (an OE tooltip), and no longer by a sentence.
        var heading = cut.Find("[data-testid='project-pipelines-readiness'] h5").ParentElement!;
        var tip = Assert.Single(heading.QuerySelectorAll("[data-testid='pipeline-info-tip']"));
        Assert.Contains("PipelineSetupReadinessCheckedOn", tip.GetAttribute("aria-label"), StringComparison.Ordinal);
        // The tip belongs to an OE tooltip, whose trigger the keyboard reaches.
        Assert.NotNull(tip.Closest(".omni-tooltip, [tabindex]"));
    }

    [Fact]
    public void ProjectPage_DrawsNothing_WhenNothingIsMissing()
    {
        var cut = Render<ProjectPipelinesReadiness>(parameters => parameters.Add(panel => panel.ProjectId, 5));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/pipelines/setup/project-readiness?projectId=5", StringComparison.Ordinal)));
        Assert.Empty(cut.FindAll("[data-testid='project-pipelines-readiness']"));
    }

    [Fact]
    public void ProjectPage_ARequiredLibraryLinksToItsOwnCreationPage()
    {
        _handler.SetJsonResponse("api/pipelines/setup/project-readiness?projectId=5", Verdict(
            Blocking(PipelineSetupReadinessKind.MissingRequiredLibraries, "atlas-host")));

        var cut = Render<ProjectPipelinesReadiness>(parameters => parameters.Add(panel => panel.ProjectId, 5));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-readiness-item-create']")));
        Assert.Equal(
            $"/variable-libraries/new?projectId=5&name=atlas-host&returnUrl={Uri.EscapeDataString("/projects/5/pipelines")}",
            cut.Find("[data-testid='setup-readiness-item-create']").GetAttribute("href"));
        Assert.DoesNotContain("/projects/5/libraries", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>R2-22: the link of a missing vault leads to the creation of that vault, its name filled
    /// in, not to the generic list of vaults; the explanation is a tooltip and the only alert is the
    /// blocking one.</summary>
    [Fact]
    public void ProjectPage_ARequiredVaultLinksToItsOwnCreationPage_AndExplainsInTooltips()
    {
        _handler.SetJsonResponse("api/pipelines/setup/project-readiness?projectId=5", Verdict(
            Blocking(PipelineSetupReadinessKind.MissingRequiredVaults, "wordmorrow.qa"),
            new PipelineSetupReadinessCheckDto
            {
                Kind = PipelineSetupReadinessKind.ExternalRepositoryNotInspected,
                Severity = PipelineSetupReadinessSeverity.Warning,
                Items = [".pipeline/scripts/ci.sh"]
            }));

        var cut = Render<ProjectPipelinesReadiness>(parameters => parameters.Add(panel => panel.ProjectId, 5));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-readiness-item-create']")));
        Assert.Equal(
            $"/vaults/new?projectId=5&name=wordmorrow.qa&returnUrl={Uri.EscapeDataString("/projects/5/pipelines")}",
            cut.Find("[data-testid='setup-readiness-item-create']").GetAttribute("href"));
        Assert.DoesNotContain("href=\"/projects/5/vaults\"", cut.Markup, StringComparison.Ordinal);
        // The vault is for the QA environment: said by a tooltip, not by a sentence in the page.
        Assert.Contains("PipelineSetupPurposeVaultQa", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[data-testid='pipeline-info-tip']"));
        // Only the blocking finding is an alert; the warning is a row.
        Assert.Single(cut.FindAll(".omni-alert"));
        Assert.Equal(2, cut.FindAll("[data-testid='setup-readiness-check']").Count);
    }

    /// <summary>Before a run: the confirmation names what is missing, Revenir stops the launch.</summary>
    [Fact]
    public async Task Run_WithAMissingAdapter_AsksFirst_AndGoingBackDoesNotLaunch()
    {
        // The shared stub returns bare keys; this one keeps the {0} the message fills with the findings.
        Services.AddSingleton<Microsoft.Extensions.Localization.IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(new PlaceholderLocalizer());
        BunitTestHelper.UseImmediateDialogs(this);
        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.ConfirmResult = false;
        _handler.SetJsonResponse("api/pipelines/5/preflight", new PipelinePreflightDto());
        _handler.SetJsonResponse("api/pipelines/setup/pipeline-readiness?pipelineId=5", Verdict(
            Blocking(PipelineSetupReadinessKind.MissingAdapterScripts, ".pipeline/scripts/ci.sh")));

        var proceed = await Services.GetRequiredService<PipelineRunGate>().ConfirmPreflightAsync(5);

        Assert.False(proceed);
        Assert.Contains("ReadinessMissingAdapterScripts : .pipeline/scripts/ci.sh", dialog.LastConfirmMessage, StringComparison.Ordinal);
        Assert.Equal("RunAnyway", dialog.LastConfirmOptions!.OkButtonText);
        Assert.Equal("GoBack", dialog.LastConfirmOptions.CancelButtonText);
    }

    [Fact]
    public async Task Run_WithAMissingAdapter_RunsAnywayWhenConfirmed()
    {
        BunitTestHelper.UseImmediateDialogs(this);
        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.ConfirmResult = true;
        _handler.SetJsonResponse("api/pipelines/5/preflight", new PipelinePreflightDto());
        _handler.SetJsonResponse("api/pipelines/setup/pipeline-readiness?pipelineId=5", Verdict(
            Blocking(PipelineSetupReadinessKind.MissingScannerConfiguration, ".gitleaks.toml")));

        Assert.True(await Services.GetRequiredService<PipelineRunGate>().ConfirmPreflightAsync(5));
        Assert.Equal(1, dialog.OpenCount);
    }

    /// <summary>A runner finding is the agent preflight's to ask about, and an external repository
    /// that could not be read is a warning, not a reason to stop: neither asks a second time.</summary>
    [Fact]
    public async Task Run_WithOnlyRunnerOrUncheckedExternalFindings_AsksNothingMore()
    {
        BunitTestHelper.UseImmediateDialogs(this);
        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse("api/pipelines/5/preflight", new PipelinePreflightDto());
        _handler.SetJsonResponse("api/pipelines/setup/pipeline-readiness?pipelineId=5", Verdict(
            Blocking(PipelineSetupReadinessKind.NoRunnerConfigured),
            new PipelineSetupReadinessCheckDto
            {
                Kind = PipelineSetupReadinessKind.ExternalRepositoryNotInspected,
                Severity = PipelineSetupReadinessSeverity.Warning,
                Items = [".pipeline/scripts/ci.sh"]
            }));

        Assert.True(await Services.GetRequiredService<PipelineRunGate>().ConfirmPreflightAsync(5));
        Assert.Equal(0, dialog.OpenCount);
    }

    [Theory]
    [InlineData("/pipelines/setup?projectId=7&step=summary", true)]
    [InlineData("https://evil.example/x", false)]
    [InlineData("//evil.example/x", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData(null, false)]
    public void ReturnAddress_IsOnlyEverALocalPath(string? returnUrl, bool kept)
    {
        Assert.Equal(kept ? returnUrl : null, LocalReturnUrl.Validated(returnUrl));
        Assert.Equal(
            kept ? $"/servers/add-agent?returnUrl={Uri.EscapeDataString(returnUrl!)}" : "/servers/add-agent",
            LocalReturnUrl.Append("/servers/add-agent", returnUrl));
        Assert.Equal(
            kept ? $"/environments/new?projectId=7&returnUrl={Uri.EscapeDataString(returnUrl!)}" : "/environments/new?projectId=7",
            LocalReturnUrl.Append("/environments/new?projectId=7", returnUrl));
    }

    private sealed class PlaceholderLocalizer : Microsoft.Extensions.Localization.IStringLocalizer<Aetheus.Front.Resources.AppStrings>
    {
        public Microsoft.Extensions.Localization.LocalizedString this[string name] =>
            new(name, name == "PipelineRunNotReadyConfirm" ? name + ": {0}" : name);
        public Microsoft.Extensions.Localization.LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(this[name].Value, arguments));
        public IEnumerable<Microsoft.Extensions.Localization.LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
