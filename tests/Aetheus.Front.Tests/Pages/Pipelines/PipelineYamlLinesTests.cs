// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
namespace Aetheus.Front.Tests.Pages;

public sealed class PipelineYamlLinesTests
{
    [Theory]
    [InlineData("pipeline: child", "pipeline: ", "child")]
    [InlineData("  pipeline: 'child-one'", "  pipeline: ", "child-one")]
    [InlineData("  - pipeline: \"child-two\"", "  - pipeline: ", "child-two")]
    public void Parse_RecognizesPipelineReferencesAndRemovesQuotes(
        string yaml,
        string expectedPrefix,
        string expectedReference)
    {
        var line = Assert.Single(PipelineYamlLines.Parse(yaml));

        Assert.Equal(expectedPrefix, line.Text);
        Assert.Equal(expectedReference, line.PipelineRef);
    }

    [Theory]
    [InlineData("pipeline_name: child")]
    [InlineData("# pipeline: child")]
    [InlineData("pipeline: child # comments are not part of a reference")]
    [InlineData("script: echo pipeline: child")]
    public void Parse_NonReference_PreservesOriginalLine(string yaml)
    {
        var line = Assert.Single(PipelineYamlLines.Parse(yaml));

        Assert.Equal(yaml, line.Text);
        Assert.Null(line.PipelineRef);
    }

    [Theory]
    [InlineData("extends: ci@2", "extends: ", "ci@2", "")]
    [InlineData("  extends: 'ci@2'", "  extends: '", "ci@2", "'")]
    [InlineData("extends: \"ci@2\"  # base", "extends: \"", "ci@2", "\"  # base")]
    [InlineData("extends: legacy-name", "extends: ", "legacy-name", "")]
    public void Parse_WithTemplateReferences_RecognizesExtendsLines(
        string yaml, string expectedPrefix, string expectedReference, string expectedSuffix)
    {
        var line = Assert.Single(PipelineYamlLines.Parse(yaml, templateReferences: true));

        Assert.Equal(expectedPrefix, line.Text);
        Assert.Equal(expectedReference, line.TemplateRef);
        Assert.Equal(expectedSuffix, line.Suffix);
        Assert.Null(line.PipelineRef);
    }

    [Fact]
    public void Parse_WithoutTemplateReferences_KeepsTheExtendsLineWhole()
    {
        var line = Assert.Single(PipelineYamlLines.Parse("extends: ci@2"));

        Assert.Equal("extends: ci@2", line.Text);
        Assert.Null(line.TemplateRef);
    }

    [Theory]
    [InlineData("# extends: ci@2")]
    [InlineData("script: echo extends: ci@2")]
    [InlineData("  extends_from: ci@2")]
    public void Parse_WithTemplateReferences_LeavesCommentsAndOtherKeysAlone(string yaml)
    {
        var line = Assert.Single(PipelineYamlLines.Parse(yaml, templateReferences: true));

        Assert.Equal(yaml, line.Text);
        Assert.Null(line.TemplateRef);
    }

    [Fact]
    public void Parse_NormalizesWindowsLineEndingsAndPreservesBlankLines()
    {
        var lines = PipelineYamlLines.Parse("name: parent\r\npipeline: child\r\n");

        Assert.Equal(3, lines.Count);
        Assert.Equal("child", lines[1].PipelineRef);
        Assert.Equal(string.Empty, lines[2].Text);
    }
}
