// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

public class StepEditDialogTests : BunitContext
{
    public StepEditDialogTests()
    {
        BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_EmptyForm_WhenNoStep()
    {
        var cut = Render<StepEditDialog>();

        Assert.Contains("StepName", cut.Markup);
        Assert.Contains("ShellCommand", cut.Markup);
        Assert.Contains("TimeoutSeconds", cut.Markup);
    }

    [Fact]
    public void Renders_PrefilledForm_WhenStepProvided()
    {
        var step = new PipelineStepDefinition
        {
            Name = "compile",
            Shell = "dotnet build",
            TimeoutSeconds = 600
        };
        var cut = Render<StepEditDialog>(p => p.Add(x => x.Step, step));

        Assert.Contains("compile", cut.Markup);
        Assert.Contains("dotnet build", cut.Markup);
        Assert.Contains("600", cut.Markup);
    }

    [Fact]
    public void Renders_SaveButton()
    {
        var cut = Render<StepEditDialog>();

        Assert.Contains("Save", cut.Markup);
    }

    [Fact]
    public void Renders_CancelButton()
    {
        var cut = Render<StepEditDialog>();

        Assert.Contains("GoBack", cut.Markup);
    }

    [Fact]
    public void SaveButton_Disabled_WhenNameEmpty()
    {
        var cut = Render<StepEditDialog>();

        var saveBtn = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Save"));
        Assert.NotNull(saveBtn);
        Assert.True(saveBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void Front014_RenamingAStep_KeepsItsConditionTypeAndOptions_ThroughTheYamlRoundTrip()
    {
        // FRONT-014: the dialog rebuilt the step with Name, Shell and TimeoutSeconds only, so renaming a
        // conditional step made it unconditional once the visual editor re-emitted the YAML.
        Services.AddSingleton<OmniDialogService>(sp => new Aetheus.Front.Tests.TestDoubles.SpyDialogService(
            sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(),
            sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
        var yaml = new YamlSerializationService();
        var definition = yaml.Parse("""
            trigger: manual
            stages:
              - name: deploy
                steps:
                  - name: publish
                    type: artifacts
                    condition: eq(variables.BRANCH, 'main')
                    working_directory: src/app
                    timeout_seconds: 900
                    retry_count: 2
                    continue_on_error: true
                    artifact_name: site
                    target_files:
                      - dist/**
            """)!;
        var original = definition.Stages[0].Steps[0];
        var cut = Render<StepEditDialog>(p => p.Add(x => x.Step, original));

        cut.Find("#oe-pages-pipelines-stepeditdialog-1").Input("publish-site");
        cut.FindAll("button").First(button => button.TextContent.Contains("Save")).Click();

        var renamed = Assert.IsType<PipelineStepDefinition>(
            ((Aetheus.Front.Tests.TestDoubles.SpyDialogService)Services.GetRequiredService<OmniDialogService>()).LastResult);
        var stages = definition.Stages.ToList();
        stages[0] = stages[0] with { Steps = [renamed] };
        var roundTripped = yaml.Parse(yaml.Serialize(definition with { Stages = stages }))!.Stages[0].Steps[0];

        Assert.Equal("publish-site", roundTripped.Name);
        Assert.Equal("eq(variables.BRANCH, 'main')", roundTripped.Condition);
        Assert.Equal("artifacts", roundTripped.Type);
        Assert.Equal("src/app", roundTripped.WorkingDirectory);
        Assert.Equal("site", roundTripped.ArtifactName);
        Assert.Equal(["dist/**"], roundTripped.TargetFiles);
        Assert.Equal(2, roundTripped.RetryCount);
        Assert.True(roundTripped.ContinueOnError);
    }

    [Fact]
    public void SaveButton_Enabled_WhenStepProvided()
    {
        var step = new PipelineStepDefinition
        {
            Name = "test",
            Shell = "dotnet test",
            TimeoutSeconds = 300
        };
        var cut = Render<StepEditDialog>(p => p.Add(x => x.Step, step));

        var saveBtn = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Save"));
        Assert.NotNull(saveBtn);
        Assert.False(saveBtn.HasAttribute("disabled"));
    }
}
