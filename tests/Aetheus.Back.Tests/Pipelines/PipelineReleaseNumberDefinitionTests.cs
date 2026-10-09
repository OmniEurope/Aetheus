// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R2-110: <c>release_number: allocate</c> is how a pipeline that produces a release (candidate,
/// release-fast) asks for the project's next release number. The key is read from YAML and takes one
/// mode, refused otherwise at save and at launch.
/// </summary>
public sealed class PipelineReleaseNumberDefinitionTests
{
    private static PipelineYamlDefinition Parse(string releaseNumberLine) =>
        YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>($"""
            name: candidate
            trigger: manual
            {releaseNumberLine}
            stages:
              - name: build
                steps:
                  - name: compile
                    shell: echo $(BUILD_RELEASENUMBER)
            """);

    private static List<string> Errors(PipelineYamlDefinition definition)
    {
        var errors = new List<string>();
        PipelineDefinitionValidator.ValidateDefinitionBasics(definition, errors, []);
        return errors;
    }

    [Fact]
    public void Allocate_IsReadFromYaml_AndValid()
    {
        var definition = Parse("release_number: allocate");

        Assert.True(PipelineReleaseNumbers.Allocates(definition));
        Assert.Empty(Errors(definition));
    }

    [Fact]
    public void Absent_AllocatesNothing()
    {
        var definition = Parse(string.Empty);

        Assert.False(PipelineReleaseNumbers.Allocates(definition));
        Assert.Empty(Errors(definition));
    }

    [Fact]
    public void AnotherMode_IsRefusedAtSaveAndAtLaunch()
    {
        var definition = Parse("release_number: next");

        Assert.Contains(PipelineReleaseNumbers.InvalidModeMessage, Errors(definition));
        Assert.Throws<BadRequestException>(() => PipelineReleaseNumbers.EnsureValid(definition));
    }
}
