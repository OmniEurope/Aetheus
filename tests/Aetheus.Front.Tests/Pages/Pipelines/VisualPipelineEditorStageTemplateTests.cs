// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Inserting a stage template appends its stages to the pipeline and emits the new YAML; a
/// pick that does not name a usable template leaves the pipeline untouched.</summary>
public sealed class VisualPipelineEditorStageTemplateTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly List<string> _emitted = [];

    public VisualPipelineEditorStageTemplateTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<YamlSerializationService>();
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>
        {
            new() { Id = 7, Name = "Deploy stage" }
        });
    }

    private const string PipelineYaml = @"stages:
- name: build
  agent: linux
  steps:
  - name: run
    shell: echo hello";

    private const string TemplateYaml = @"stages:
- name: deploy
  agent: linux
  steps:
  - name: ship
    shell: echo ship";

    // Through the toolbar's stage-template picker, as the reader picks one.
    private async Task InsertAsync(int pick)
    {
        var cut = Render<VisualPipelineEditor>(p => p
            .Add(x => x.YamlDefinition, PipelineYaml)
            .Add(x => x.YamlDefinitionChanged, yaml => _emitted.Add(yaml))
            .Add(x => x.AvailableServers, ["linux"])
            .Add(x => x.AvailableLibraries, [])
            .Add(x => x.AvailableVaults, []));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.EndsWith("api/pipelines/templates", StringComparison.Ordinal)));
        var picker = cut.FindComponent<OmniDropDown<int?>>();
        await cut.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(pick));
    }

    [Fact]
    public async Task ATemplate_AppendsItsStagesAfterThePipelineOnes()
    {
        _handler.SetJsonResponse("api/pipelines/templates/7", new PipelineTemplateDto { Id = 7, Name = "Deploy stage", YamlContent = TemplateYaml });

        await InsertAsync(0);

        var yaml = Assert.Single(_emitted);
        var build = yaml.IndexOf("name: build", StringComparison.Ordinal);
        var deploy = yaml.IndexOf("name: deploy", StringComparison.Ordinal);
        Assert.True(build >= 0 && deploy > build, yaml);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task APickThatNamesNoTemplate_ChangesNothing(int pick)
    {
        await InsertAsync(pick);

        Assert.Empty(_emitted);
        Assert.DoesNotContain(_handler.Requests, r => r.Url.EndsWith("api/pipelines/templates/7", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"id":7,"name":"Empty","yamlContent":"stages: []"}""")]
    public async Task ATemplateWithoutStages_ChangesNothing(string body)
    {
        _handler.SetRawResponse("api/pipelines/templates/7", body);

        await InsertAsync(0);

        Assert.Contains(_handler.Requests, r => r.Url.EndsWith("api/pipelines/templates/7", StringComparison.Ordinal));
        Assert.Empty(_emitted);
    }
}
