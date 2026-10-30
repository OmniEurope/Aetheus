// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-105: the setup wizard asks which languages the quality pipeline analyses, and pre-ticks the ones the
/// project's repository holds a file of. The readiness check's detection answers with the same rule as the
/// pipeline's own language detection (a product file below the root, outside the directories that never hold product
/// code); a repository it could not read to the end answers "not told", never "absent".
/// </summary>
public sealed class PipelineSetupLanguageDetectionTests
{
    private const int RepositoryId = 4;

    private readonly IPipelineTemplateService _templates = Substitute.For<IPipelineTemplateService>();
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();

    public PipelineSetupLanguageDetectionTests()
    {
        _repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([11]);
        _repo.HasRunnerWithDockerAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        _git.GetRepositoriesAsync(7, Arg.Any<CancellationToken>()).Returns(
            new List<GitLightRepoDto> { new() { Id = RepositoryId, ProjectId = 7, Name = "app", DefaultBranch = "main" } });
        _git.GetTreeAsync(RepositoryId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<GitLightTreeEntryDto>());
        _repo.FindSharedTemplateByNameAsync("application-quality", Arg.Any<CancellationToken>())
            .Returns(new Aetheus.Back.Data.Entities.PipelineTemplate { Id = 2, Name = "application-quality" });
        _templates.GetTemplateAsync(2, Arg.Any<CancellationToken>()).Returns(new PipelineTemplateDto
        {
            Id = 2,
            Name = "application-quality",
            YamlContent = """
                name: application-quality-template
                variables:
                  APPLICATION_QUALITY_COMMANDS: "default"
                stages:
                  - name: Build
                    execution_role: build
                    steps:
                      - name: Run
                        shell: echo ok
                """
        });
    }

    /// <summary>One directory of the branch: the names ending with a slash are sub-directories.</summary>
    private void Dir(string? path, params string[] names) =>
        _git.GetTreeAsync(RepositoryId, Arg.Any<string?>(), Arg.Is<string?>(candidate => candidate == path), Arg.Any<CancellationToken>())
            .Returns([.. names.Select(name =>
            {
                var directory = name.EndsWith('/');
                var bare = name.TrimEnd('/');
                return new GitLightTreeEntryDto
                {
                    Name = bare,
                    Path = path is null ? bare : $"{path}/{bare}",
                    Type = directory ? GitTreeEntryType.Tree : GitTreeEntryType.Blob
                };
            })]);

    private async Task<PipelineSetupDetectionDto> DetectAsync()
    {
        var service = new PipelineSetupReadinessService(
            _templates, _repo, _git, Substitute.For<IGitLightRepository>(), Substitute.For<IPipelineTemplateResolver>(),
            Substitute.For<IPipelineRequirementsChecker>(), Substitute.For<IProjectConfigRepositoryService>(),
            Substitute.For<ILogger<PipelineSetupReadinessService>>());
        return (await service.CheckAsync(7, ["application-quality"], organizationId: 3, CancellationToken.None)).Detection;
    }

    [Fact]
    public async Task ADotNetAndPythonRepository_ReportsBoth_AndNoJava()
    {
        Dir(null, "src/", "tools/");
        Dir("src", "App.csproj");
        Dir("tools", "release.py");

        var detection = await DetectAsync();

        Assert.True(detection.HasDotNetProject);
        Assert.True(detection.HasPython);
        Assert.False(detection.HasJava);
    }

    [Fact]
    public async Task AJavaOnlyRepository_ReportsJava_AndNeitherDotNetNorPython()
    {
        Dir(null, "app/");
        Dir("app", "src/");
        Dir("app/src", "Main.java");

        var detection = await DetectAsync();

        Assert.True(detection.HasJava);
        Assert.False(detection.HasDotNetProject);
        Assert.False(detection.HasPython);
    }

    [Fact]
    public async Task AFileInADirectoryThatNeverHoldsProductCode_DoesNotCount()
    {
        Dir(null, "node_modules/", ".venv/", "tests/", "build/", "src/");
        Dir("node_modules", "setup.py");
        Dir(".venv", "site.py");
        Dir("build", "Gen.java");
        Dir("tests", "security-rules/");
        Dir("tests/security-rules", "sample.py", "Sample.java");
        Dir("src", "App.csproj");

        var detection = await DetectAsync();

        Assert.True(detection.HasDotNetProject);
        Assert.False(detection.HasPython);
        Assert.False(detection.HasJava);
    }

    [Fact]
    public async Task ARepositoryTheBudgetCouldNotListToTheEnd_ToldWhatItFoundAndNothingAboutTheRest()
    {
        // 130 directories at the root: more listings than the budget allows, none of them holds a Python file.
        Dir(null, [.. Enumerable.Range(0, 130).Select(index => $"d{index}/"), "Program.csproj"]);

        var detection = await DetectAsync();

        Assert.True(detection.HasDotNetProject);
        Assert.Null(detection.HasPython);
        Assert.Null(detection.HasJava);
    }

    [Fact]
    public async Task ARepositoryThatCannotBeRead_ToldNothing()
    {
        _git.GetRepositoriesAsync(7, Arg.Any<CancellationToken>()).Returns(new List<GitLightRepoDto>());

        var detection = await DetectAsync();

        Assert.Null(detection.HasDotNetProject);
        Assert.Null(detection.HasPython);
        Assert.Null(detection.HasJava);
    }
}
