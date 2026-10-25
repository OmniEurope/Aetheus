// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Importing a pipeline from a YAML file in the global pipelines list: the file is validated by the
/// backend for its name, stashed for the new-pipeline form, which opens; an unvalidated file keeps a blank
/// name, and an empty file opens nothing.
/// </summary>
public sealed class PipelinesListImportTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelinesListImportTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/pipelines/dependencies", new PipelineDependencyGroupsDto());
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        _handler.SetJsonResponse("api/pipelines/favorites", new PipelineFavoritesDto());
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos", Array.Empty<GitLightRepoDto>());
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderWithImporterOpen()
    {
        var host = this.RenderWithOutlets<PipelinesList>(new Dictionary<string, object?>(), PageHeaderActions.SectionName);
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".omni-overflow-menu__trigger")));
        var import = host.FindMenuItem("Import");
        Assert.NotNull(import);
        import.Click();
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindComponents<InputFile>()));
        return host;
    }

    private Bunit.TestDoubles.BunitNavigationManager Navigation =>
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

    [Fact]
    public void AValidatedFile_IsStashedWithItsName_AndOpensTheNewPipelineForm()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/validate", new PipelineYamlDefinition { Name = "deploy-web" });
        var host = RenderWithImporterOpen();

        host.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromText("name: deploy-web\nstages: []", "deploy.yaml", contentType: "application/x-yaml"));

        host.WaitForAssertion(() => Assert.EndsWith("/pipelines/new", Navigation.Uri, StringComparison.Ordinal));
        var stashed = Services.GetRequiredService<PipelineImportState>().Consume();
        Assert.Equal(("name: deploy-web\nstages: []", "deploy-web"), stashed);
        Assert.Contains("deploy-web", _handler.RequestDetails.Single(request => request.Method == "POST").Body, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileTheBackendCannotValidate_IsStashedWithABlankName()
    {
        _handler.SetResponse(HttpMethod.Post, "api/pipelines/validate", System.Net.HttpStatusCode.ServiceUnavailable);
        var host = RenderWithImporterOpen();

        host.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromText("stages: []", "deploy.yaml", contentType: "application/x-yaml"));

        host.WaitForAssertion(() => Assert.EndsWith("/pipelines/new", Navigation.Uri, StringComparison.Ordinal));
        Assert.Equal(("stages: []", (string?)null), Services.GetRequiredService<PipelineImportState>().Consume());
    }

    [Fact]
    public void ABlankFile_OpensNothing()
    {
        var host = RenderWithImporterOpen();
        var before = Navigation.Uri;

        host.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromText("   \n  ", "blank.yaml", contentType: "application/x-yaml"));

        Assert.Equal(before, Navigation.Uri);
        Assert.Null(Services.GetRequiredService<PipelineImportState>().Consume());
        Assert.DoesNotContain(_handler.Requests, request => request.Method == "POST");
    }
}
