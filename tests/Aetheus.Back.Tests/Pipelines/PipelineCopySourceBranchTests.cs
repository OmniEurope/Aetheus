// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-76: a copied pipeline reads the branch of the project it is copied into, not the one its source project used.</summary>
public sealed class PipelineCopySourceBranchTests
{
    [Fact]
    public void Rewrite_ReplacesTheTopLevelBranch_AndOnlyThatLine()
    {
        const string yaml = "name: ats-ci\nextends: application-ci@3\ntrigger: manual\nsource_branch: \"develop\"\nvariables:\n  A: \"b\"\nstages: []\n";

        var rewritten = PipelineCopySourceBranch.Rewrite(yaml, "main");

        Assert.Equal(yaml.Replace("\"develop\"", "\"main\"", StringComparison.Ordinal), rewritten);
    }

    [Theory]
    [InlineData("source_branch: develop")]
    [InlineData("source_branch: 'develop'")]
    [InlineData("source_branch:    \"develop\"   # the work branch")]
    public void Rewrite_AcceptsEveryWayTheValueIsWritten(string line)
    {
        var rewritten = PipelineCopySourceBranch.Rewrite($"name: a\n{line}\nstages: []\n", "release/1.0");

        Assert.Equal("name: a\nsource_branch: \"release/1.0\"\nstages: []\n", rewritten);
    }

    [Fact]
    public void Rewrite_KeepsTheLineEndingsOfTheDefinition()
    {
        var rewritten = PipelineCopySourceBranch.Rewrite("name: a\r\nsource_branch: \"develop\"\r\nstages: []\r\n", "main");

        Assert.Equal("name: a\r\nsource_branch: \"main\"\r\nstages: []\r\n", rewritten);
    }

    [Fact]
    public void Rewrite_LeavesTheBranchOfAStepAlone()
    {
        const string yaml = "name: a\nsource_branch: \"develop\"\nstages:\n  - name: s\n    steps:\n      - name: t\n        type: trigger\n        inherit_source: false\n        source_branch: develop\n";

        var rewritten = PipelineCopySourceBranch.Rewrite(yaml, "main");

        Assert.Contains("\nsource_branch: \"main\"\n", rewritten, StringComparison.Ordinal);
        Assert.Contains("        source_branch: develop\n", rewritten, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rewrite_WithoutABranch_RemovesTheLine_AsTheWizardWritesNone(string? branch)
    {
        var rewritten = PipelineCopySourceBranch.Rewrite("name: a\nsource_branch: \"develop\"\nstages: []\n", branch);

        Assert.Equal("name: a\nstages: []\n", rewritten);
    }

    [Fact]
    public void Rewrite_ADefinitionWithoutTheKey_IsReturnedAsItIs()
    {
        const string yaml = "name: a\ntrigger: manual\nstages: []\n";

        Assert.Equal(yaml, PipelineCopySourceBranch.Rewrite(yaml, "main"));
    }

    [Theory]
    [InlineData("x\"\ninjected: true")]
    [InlineData("a..b")]
    [InlineData("-leading")]
    public void Rewrite_RefusesABranchThatIsNotABranchName_BeforeWritingItIntoTheDefinition(string branch)
    {
        Assert.Throws<BadRequestException>(() => PipelineCopySourceBranch.Rewrite("name: a\nsource_branch: \"develop\"\n", branch));
    }
}
