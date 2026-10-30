// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// R-79: the Save button of the pipeline page's Settings tab, on a Git-managed pipeline that declares
/// parameters (as the W7 ones do), opens the review dialog without any exception reaching the framework (the
/// Blazor error bar "Une erreur inattendue est survenue" is what such an exception shows in the browser).
/// The cause was the dialog's commit-branch dropdown, rendered without a ValueExpression, which only a
/// pipeline with branches shows. The real dialog host renders the real review dialog; the browser-side
/// Monaco frame is the one part bUnit has no browser for.
/// </summary>
public class PipelineEditSaveWithParametersTests : BunitContext
{
    private const string ParametersYaml = """
        name: w7-deploy
        trigger: manual
        parameters:
          - name: revision
            type: string
          - name: environment
            type: choice
            required: true
            allowed_values: [qa, production]
          - name: dry_run
            type: boolean
            default: "false"
        stages: []
        """;

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineEditSaveWithParametersTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<YamlSerializationService>();
        _handler.SetJsonResponse("api/ai/profile-options", new List<AiRunnerProfileDto>());
        _handler.SetJsonResponse("api/pipelines/5", new PipelineDto
        {
            Id = 5,
            Name = "w7-deploy",
            Description = "W7",
            YamlDefinition = ParametersYaml,
            TriggerType = PipelineTriggerType.Manual
        });
        _handler.SetJsonResponse("api/pipelines/5/runs", new PaginatedResult<PipelineRunDto>());
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        // A Git-managed pipeline (as the W7 ones): the review dialog then offers the commit branch.
        _handler.SetJsonResponse("api/pipelines/5/source", new PipelineSourceDto
        {
            RepositoryId = 9,
            Path = ".pipeline/w7-deploy.yaml",
            Branch = "main",
            CommitHash = "0123456789abcdef0123456789abcdef01234567"
        });
        _handler.SetPaginatedJsonResponse("api/git/repos/9/branches", new List<GitLightBranchDto>
        {
            new() { Name = "main", IsDefault = true },
            new() { Name = "develop" }
        });
        _handler.SetJsonResponse("api/pipelines/5/repository-definition", new PipelineRepositoryDefinitionDto
        {
            Exists = true,
            Yaml = ParametersYaml,
            Sha256 = "abc"
        });
        _handler.SetJsonResponse(HttpMethod.Put, "api/pipelines/5", new PipelineDto { Id = 5, Name = "w7-deploy", YamlDefinition = ParametersYaml });
    }

    [Fact]
    public async Task R79_TheSaveClickOfTheSettingsTab_OnAGitManagedPipeline_OpensTheReviewDialogWithItsBranchChoice_WithoutAnyUnhandledException()
    {
        var overlay = Services.GetRequiredService<OmniOverlayService>();
        var host = Render<OmniComponentsHost>(parameters => parameters.Add(item => item.OverlayService, overlay));
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/pipelines/5?tab=settings");
        var cut = Render<PipelineEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Markup.Contains("w7-deploy", StringComparison.Ordinal));

        var save = cut.FindAll("button").First(button => button.TextContent.Trim() == "SaveSettings");
        var submit = save.ClickAsync(new MouseEventArgs());

        host.WaitForAssertion(() => Assert.NotEmpty(host.FindComponents<PipelineSaveDialog>()));
        host.WaitForAssertion(() => Assert.DoesNotContain("PipelineSaveRepositoryLoading", host.Markup, StringComparison.Ordinal));
        host.FindComponent<PipelineSaveDialog>().FindAll("button")
            .First(button => button.TextContent.Contains("Save", StringComparison.Ordinal)).Click();
        await submit;

        Assert.Contains(_handler.Requests, request => request.Method == "PUT" && request.Url.Contains("api/pipelines/5", StringComparison.Ordinal));
    }
}
