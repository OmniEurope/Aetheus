// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Importing a template from a YAML file chosen in the templates page: the file is posted as is, the
/// importer closes, the import is confirmed and the list is read again; a refused import keeps the
/// importer open and claims nothing.
/// </summary>
public sealed class PipelineTemplatesImportTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineTemplatesImportTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/templates", new List<PipelineTemplateSummaryDto>
        {
            new() { Id = 7, Name = "Build Template", Category = "CI", Version = 3 }
        });
    }

    private IRenderedComponent<PipelineTemplates> RenderWithImporterOpen()
    {
        var cut = Render<PipelineTemplates>();
        cut.WaitForState(() => cut.Markup.Contains("Build Template", StringComparison.Ordinal));
        var import = cut.FindMenuItem("Import");
        Assert.NotNull(import);
        import.Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindComponents<InputFile>()));
        return cut;
    }

    private int ListReads() =>
        _handler.Requests.Count(request => request.Method == "GET"
            && request.Url.EndsWith("api/pipelines/templates", StringComparison.Ordinal));

    private string? ImportBody() =>
        _handler.RequestDetails.SingleOrDefault(request => request.Method == "POST"
            && request.Url.EndsWith("api/pipelines/templates/import", StringComparison.Ordinal)).Body;

    [Fact]
    public void AnImportedFile_IsPosted_ClosesTheImporter_ConfirmsAndReloadsTheList()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/templates/import",
            new PipelineTemplateDto { Id = 8, Name = "imported", Version = 1, YamlContent = "stages: []" });
        var cut = RenderWithImporterOpen();
        var readsBefore = ListReads();

        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromText("stages: []", "imported.yaml", contentType: "application/x-yaml"));

        cut.WaitForAssertion(() => Assert.Contains(Services.Toasts(), toast => toast.Summary == "Imported"));
        var body = ImportBody();
        Assert.NotNull(body);
        Assert.Contains("imported.yaml", body, StringComparison.Ordinal);
        Assert.Contains("stages: []", body, StringComparison.Ordinal);
        Assert.Empty(cut.FindComponents<InputFile>());
        Assert.Equal(readsBefore + 1, ListReads());
    }

    [Fact]
    public void ARefusedImport_KeepsTheImporterOpen_AndReloadsNothing()
    {
        _handler.SetResponse(HttpMethod.Post, "api/pipelines/templates/import", System.Net.HttpStatusCode.BadRequest);
        var cut = RenderWithImporterOpen();
        var readsBefore = ListReads();

        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromText("not: [valid", "broken.yaml", contentType: "application/x-yaml"));

        cut.WaitForAssertion(() => Assert.NotNull(ImportBody()));
        Assert.NotEmpty(cut.FindComponents<InputFile>());
        Assert.Equal(readsBefore, ListReads());
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Summary == "Imported");
    }
}
