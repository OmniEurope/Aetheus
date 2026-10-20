// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>The coverage tab's project tree and its gauge colours.</summary>
public sealed class CoverageProjectTreeTests
{
    [Theory]
    [InlineData("Aetheus.Back", "anything/else.cs", "Aetheus.Back")]
    [InlineData("", "src/Aetheus.Back/Components/Pipelines/A.cs", "Aetheus.Back")]
    [InlineData("", "/home/agent/work/1/s/src/Aetheus.Front/Pages/B.razor", "Aetheus.Front")]
    [InlineData("", @"C:\agent\_work\tests\Aetheus.Back.Tests\C.cs", "Aetheus.Back.Tests")]
    [InlineData("", "Tools/Normalizer/D.cs", "Tools")]
    [InlineData("", "E.cs", CoverageProjectName.Root)]
    public void ProjectName_IsThePackage_ElseTheDirectoryThePathNames(string assembly, string file, string expected)
    {
        Assert.Equal(expected, CoverageProjectName.Of(new CoverageFileDto { Assembly = assembly, File = file }));
    }

    [Fact]
    public void Build_GroupsFilesByProject_WithSummedLines_LeastCoveredFirstAtBothLevels()
    {
        var projects = CoverageProjectTree.Build(
        [
            new CoverageFileDto { File = "src/Web/Good.cs", LineRate = 1, LinesCovered = 10, LinesValid = 10 },
            new CoverageFileDto { File = "src/Api/Low.cs", LineRate = 0.1, LinesCovered = 1, LinesValid = 10 },
            new CoverageFileDto { File = "src/Api/Mid.cs", LineRate = 0.5, LinesCovered = 5, LinesValid = 10 }
        ]);

        Assert.Equal(["Api", "Web"], projects.Select(project => project.Name));
        var api = projects[0];
        Assert.Equal(6, api.LinesCovered);
        Assert.Equal(20, api.LinesValid);
        Assert.Equal(0.3, api.LineRate, 3);
        Assert.Equal(["src/Api/Low.cs", "src/Api/Mid.cs"], api.Files.Select(file => file.File));
    }

    [Theory]
    [InlineData(0.95, 1)]
    [InlineData(0.8, 1)]
    [InlineData(0.79, 2)]
    [InlineData(0.5, 2)]
    [InlineData(0.49, 4)]
    public void GaugeColour_FollowsTheTiersOfTheFigureBesideIt(double rate, int expected)
    {
        // OE coverage palette indices 1, 2 and 4 denote green, orange and red.
        Assert.Equal(expected, PipelineRunFormatting.CoverageChartColorIndex(rate));
        var textTier = PipelineRunFormatting.CoverageColorClass(rate);
        Assert.Equal(expected switch { 1 => "omni-u-text-success", 2 => "omni-u-text-warning", _ => "omni-u-text-danger" }, textTier);
    }
}
