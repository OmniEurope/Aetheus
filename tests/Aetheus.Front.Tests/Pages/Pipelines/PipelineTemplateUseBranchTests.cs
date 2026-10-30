// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-76 (d): the "use this template" route that opens the pipeline editor (<c>/pipelines/new?templateId=</c>, the
/// templates that are not one of the wizard's roles) writes <c>name</c>, <c>extends</c> and the parameters only. It writes no
/// <c>source_branch:</c>, so there is no branch to choose there: the pipeline's source branch is the editor's own field.</summary>
public sealed class PipelineTemplateUseBranchTests : BunitContext
{
    [Fact]
    public async Task TheYamlTheEditorStartsFromHasNoSourceBranch()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse("api/pipelines/templates/4", new PipelineTemplateDto
        {
            Id = 4,
            Name = "custom-ci",
            Version = 2,
            YamlContent = "name: custom-ci\nstages:\n  - name: build\n    agent: any\n    steps:\n      - name: compile\n        shell: dotnet build\n"
        });
        var coordinator = new PipelineTemplateEditorCoordinator(Services.GetRequiredService<ApiClient>());

        var selection = await coordinator.SelectAsync(4, "ats-ci");

        Assert.NotNull(selection);
        Assert.Contains("extends: custom-ci@2", selection.Yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("source_branch", selection.Yaml, StringComparison.Ordinal);
    }
}
