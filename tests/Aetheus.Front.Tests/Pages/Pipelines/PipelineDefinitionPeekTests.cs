// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public sealed class PipelineDefinitionPeekTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineDefinitionPeekTests() => _handler = BunitTestHelper.RegisterServices(this);

    [Fact]
    public void FuzzySearchResult_DoesNotDisplayDifferentPipeline()
    {
        _handler.SetJsonResponse("api/pipelines", new PaginatedResult<PipelineDto>
        {
            Items = [new PipelineDto { Id = 8, Name = "deploy-production", YamlDefinition = "wrong" }],
            TotalCount = 1,
            Page = 1,
            PageSize = 100
        });

        var cut = Render<PipelineDefinitionPeek>(parameters => parameters
            .Add(component => component.PipelineName, "deploy")
            .Add(component => component.ProjectId, 4));

        cut.WaitForAssertion(() => Assert.Contains("PipelineNotFound", cut.Markup));
        Assert.DoesNotContain(_handler.Requests, request =>
            request.Url.Contains("api/pipelines/8", StringComparison.Ordinal));
        Assert.Null(typeof(PipelineDefinitionPeek).GetField("_current",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance));
    }

    [Fact]
    public void ExactCaseInsensitiveMatch_LoadsRequestedDefinition()
    {
        _handler.SetJsonResponse("api/pipelines", new PaginatedResult<PipelineDto>
        {
            Items = [new PipelineDto { Id = 9, Name = "Deploy", YamlDefinition = string.Empty }],
            TotalCount = 1,
            Page = 1,
            PageSize = 100
        });
        _handler.SetJsonResponse("api/pipelines/9", new PipelineDto
        {
            Id = 9,
            Name = "Deploy",
            YamlDefinition = "name: exact"
        });

        var cut = Render<PipelineDefinitionPeek>(parameters => parameters
            .Add(component => component.PipelineName, "deploy"));

        cut.WaitForAssertion(() => Assert.Contains("name: exact", cut.Markup));
        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/pipelines/9", StringComparison.Ordinal));
    }

    private void SetTemplateCatalog()
    {
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>
        {
            new() { Id = 7, Name = "ci", Version = 3 },
            new() { Id = 8, Name = "root", Version = 1 }
        });
        _handler.SetJsonResponse("api/pipelines/templates/7/versions/2", new PipelineTemplateVersionDto
        {
            Version = 2,
            YamlContent = "name: ci-v2\nextends: 'root@1'  # the base\n"
        });
        _handler.SetJsonResponse("api/pipelines/templates/7", new PipelineTemplateDto
        {
            Id = 7,
            Name = "ci",
            Version = 3,
            YamlContent = "name: ci-latest\nextends: root@1\n"
        });
        _handler.SetJsonResponse("api/pipelines/templates/8", new PipelineTemplateDto
        {
            Id = 8,
            Name = "root",
            Version = 1,
            YamlContent = "name: root-base\nvariables:\n  VAULT: shared\n"
        });
    }

    /// <summary>R-35: the template version an <c>extends:</c> line names opens (not the latest), and the
    /// <c>extends:</c> line of that template is itself a link that opens the template it inherits from.</summary>
    [Fact]
    public void TemplateReference_OpensThePinnedVersion_AndItsOwnExtendsCanBeFollowed()
    {
        SetTemplateCatalog();

        var cut = Render<PipelineDefinitionPeek>(parameters => parameters
            .Add(component => component.TemplateReference, "ci@2"));

        cut.WaitForAssertion(() => Assert.Contains("name: ci-v2", cut.Markup));
        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/pipelines/templates/7/versions/2", StringComparison.Ordinal));
        Assert.DoesNotContain("ci-latest", cut.Markup);
        Assert.NotNull(cut.Find("[data-testid='template-peek']"));
        Assert.Equal("ci@2", cut.Find(".run-logs-header strong, .run-logs-header .omni-text").TextContent.Trim());
        Assert.Equal("/templates/7", cut.Find(".run-logs-header a.pipeline-name-link").GetAttribute("href"));

        var link = cut.Find(".yaml-pipeline-link");
        Assert.Equal("root@1", link.TextContent);
        link.Click();

        cut.WaitForAssertion(() => Assert.Contains("name: root-base", cut.Markup));
        Assert.Equal("root@1", cut.Find(".run-logs-header strong, .run-logs-header .omni-text").TextContent.Trim());
        Assert.Equal("/templates/8", cut.Find(".run-logs-header a.pipeline-name-link").GetAttribute("href"));
    }

    /// <summary>R-35: a bare (legacy) reference means the latest version, and the pane says which version that is.</summary>
    [Fact]
    public void BareTemplateReference_OpensTheLatestVersion_AndNamesIt()
    {
        SetTemplateCatalog();

        var cut = Render<PipelineDefinitionPeek>(parameters => parameters
            .Add(component => component.TemplateReference, "ci"));

        cut.WaitForAssertion(() => Assert.Contains("name: ci-latest", cut.Markup));
        Assert.Equal("ci@3", cut.Find(".run-logs-header strong, .run-logs-header .omni-text").TextContent.Trim());
    }

    [Fact]
    public void UnknownTemplate_SaysTemplateNotFound()
    {
        SetTemplateCatalog();

        var cut = Render<PipelineDefinitionPeek>(parameters => parameters
            .Add(component => component.TemplateReference, "missing@1"));

        cut.WaitForAssertion(() => Assert.Contains("TemplateNotFound", cut.Markup));
        Assert.DoesNotContain("PipelineNotFound", cut.Markup);
    }
}
