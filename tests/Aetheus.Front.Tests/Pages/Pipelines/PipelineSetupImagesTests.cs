// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-51 (user decision 2026-11-03): the wizard no longer offers the "Container images" tile nor the
/// mandatory archives field, so nothing on the security step blocks "Next" and the security pipeline is written with
/// the template's image defaults (off). The builder keeps the seam a future automatic detection will use. These tests
/// cover what the wizard shows and writes, not a pipeline run.</summary>
public sealed class PipelineSetupImagesTests : BunitContext
{
    private const string Tile = "setup-option-container-images";
    private const string Archives = "setup-image-archives";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupImagesTests() => _handler = BunitTestHelper.RegisterServices(this);

    private void Arrange()
    {
        _handler.SetPaginatedJsonResponse("api/projects", [new ProjectDto { Id = 7, Name = "Atlas", DefaultBranch = "develop" }]);
        _handler.SetJsonResponse("api/pipelines/templates", TemplateCatalog());
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines", new PipelineDto { Id = 91 });
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/setup/readiness",
            new PipelineSetupReadinessDto { RepositoryInspected = true, InspectedBranch = "develop" });
    }

    private async Task<IRenderedComponent<PipelineSetupWizard>> OpenCapabilitiesAsync()
    {
        Arrange();
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/pipelines/setup?projectId=7");
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Atlas", cut.Markup, StringComparison.Ordinal));
        var wizard = cut.FindComponent<WizardDialog>();
        await cut.InvokeAsync(wizard.Instance.NextStep);
        await PipelineSetupWizardTests.ChooseArchitectureAsync(cut, "application", sameProject: false);
        await cut.InvokeAsync(wizard.Instance.NextStep);
        return cut;
    }

    private IRenderedComponent<PipelineSetupWizard> OpenSummaryFrom(string options, string? images)
    {
        Arrange();
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"http://localhost/pipelines/setup?projectId=7&prefix=atlas&options={Uri.EscapeDataString(options)}"
            + "&architecture=application&sameProject=no&step=summary"
            + (images is null ? string.Empty : $"&images={Uri.EscapeDataString(images)}"));
        var cut = Render<PipelineSetupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("atlas-security", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static Task ClickAsync(IRenderedComponent<PipelineSetupWizard> cut, string testId) =>
        cut.Find($"[data-testid='{testId}']").ClickAsync(new MouseEventArgs());

    private static bool NextIsDisabled(IRenderedComponent<PipelineSetupWizard> cut) =>
        cut.Find(".omni-wizard__actions button.omni-wizard__next").HasAttribute("disabled");

    private async Task<CreatePipelineRequest[]> FinishFromCapabilitiesAsync(IRenderedComponent<PipelineSetupWizard> cut)
    {
        await cut.InvokeAsync(cut.FindComponent<WizardDialog>().Instance.NextStep);
        cut.WaitForAssertion(() => Assert.Contains("setup-summary", cut.Markup, StringComparison.Ordinal));
        return await FinishAsync(cut);
    }

    private async Task<CreatePipelineRequest[]> FinishAsync(IRenderedComponent<PipelineSetupWizard> cut)
    {
        await cut.Find(".omni-wizard__actions button.omni-wizard__finish").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.NotEmpty(Created()));
        return Created();
    }

    private CreatePipelineRequest[] Created() => _handler.RequestDetails
        .Where(request => request.Method == HttpMethod.Post.Method
            && request.Url.EndsWith("api/pipelines", StringComparison.OrdinalIgnoreCase))
        .Select(request => JsonSerializer.Deserialize<CreatePipelineRequest>(request.Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
        .ToArray();

    private static string SecurityYaml(CreatePipelineRequest[] created) =>
        Assert.Single(created, request => request.Name == "atlas-security").YamlDefinition;

    [Fact]
    public async Task TheSecurityStep_OffersNoImageTileNorArchivesField_AndNextIsNotBlocked()
    {
        var cut = await OpenCapabilitiesAsync();

        Assert.Empty(cut.FindAll($"[data-testid='{Tile}']"));
        Assert.Empty(cut.FindAll($"[data-testid='{Archives}']"));
        Assert.Empty(cut.FindAll("[data-testid='setup-images']"));
        Assert.False(NextIsDisabled(cut));
    }

    [Fact]
    public async Task TheSecurityPipeline_IsWrittenWithoutImageVariables()
    {
        var cut = await OpenCapabilitiesAsync();

        var created = await FinishFromCapabilitiesAsync(cut);

        Assert.DoesNotContain("APPLICATION_HAS_IMAGES", SecurityYaml(created), StringComparison.Ordinal);
        Assert.DoesNotContain("APPLICATION_IMAGE_", SecurityYaml(created), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOldSummaryAddress_NamingTheImageOption_StillOpensAndWritesNoImageVariables()
    {
        var cut = OpenSummaryFrom("backend,database,ci,security,container-images", "atlas-back.tar.gz");

        var created = await FinishAsync(cut);

        Assert.DoesNotContain("APPLICATION_HAS_IMAGES", SecurityYaml(created), StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnpackedName_DropsTheGzSuffix() =>
        Assert.Equal("b.tar", PipelineSetupImages.UnpackedName("b.tar.gz"));

    [Fact]
    public void TheBuilder_WritesTheImageVariablesOnlyForTheSecurityTemplate()
    {
        var input = Input(["atlas-back.tar.gz", "atlas-front.tar.gz"]);

        var security = PipelineSetupYamlBuilder.Build("atlas-security", "application-security", 4, input);
        var ci = PipelineSetupYamlBuilder.Build("atlas-ci", "application-ci", 4, input);

        Assert.Contains("APPLICATION_HAS_IMAGES: \"true\"", security, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_IMAGE_TARS: \"atlas-back.tar atlas-front.tar\"", security, StringComparison.Ordinal);
        Assert.DoesNotContain("IMAGE", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("IMAGE", PipelineSetupYamlBuilder.Build("atlas-security", "application-security", 4, Input(null)), StringComparison.Ordinal);
    }

    private static PipelineSetupYamlInput Input(IReadOnlyList<string>? archives) => new(
        "atlas", (true, true, false), "package-web-analytics-dotnet",
        Rollback: true, Quality: true, Security: true, Qa: true, QaExtended: false, SecurityHistory: false,
        ["ci", "security"], ImageArchives: archives);

    private static List<PipelineTemplateSummaryDto> TemplateCatalog() =>
    [
        .. new[]
        {
            "application-ci", "application-quality", "application-security", "application-qa",
            "application-candidate", "application-nightly", "application-deploy-prod", "application-deploy-prod-front-back"
        }.Select(name => new PipelineTemplateSummaryDto
        {
            Id = name.GetHashCode(StringComparison.Ordinal),
            Name = name,
            Version = 1
        })
    ];
}
