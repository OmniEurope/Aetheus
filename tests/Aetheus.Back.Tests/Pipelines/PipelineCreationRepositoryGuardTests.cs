// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Aetheus.Back.Services.Email;
using Aetheus.Back.Tests.Git;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R-95, creation paths: creating a pipeline, or copying an environment's pipelines into a
/// project, must not overwrite a definition file the project's repository already holds with other
/// content. These tests run the real <see cref="PipelineService"/> / <see cref="PipelineGitService"/>
/// and the real git writer against a temporary bare repository; only the database repository and the
/// project-repository lookup are doubles (NSubstitute).
/// </summary>
public sealed class PipelineCreationRepositoryGuardTests : IDisposable
{
    private const string Branch = "develop";

    private const string AetheusYaml = "name: deploy\ntrigger: manual\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: dotnet build\n";

    // What was pushed by hand at the same path: two variables Aetheus never saw.
    private const string RepositoryYaml = "name: deploy\ntrigger: manual\nvariables:\n  COMPOSE_FILE: deploy/compose.yml\n  DEPLOY_HAS_API: \"true\"\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: dotnet build\n";

    private readonly string _bare = Path.Combine(Path.GetTempPath(), $"pipeline-create-guard-{Guid.NewGuid():N}.git");
    private readonly GitLightCliService _cli;
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly PipelineService _service;
    private readonly PipelineGitService _git;

    public PipelineCreationRepositoryGuardTests()
    {
        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance);
        _cli = new GitLightCliService(
            runner,
            new GitLightCliWriter(runner, NullLogger<GitLightCliWriter>.Instance),
            NullLogger<GitLightCliService>.Instance,
            TimeProvider.System);
        RunGit($"init --bare --initial-branch {Branch} \"{_bare}\"");
        GitFixtureGuard.AssertOwnedBy(_bare, _bare);

        var gitService = Substitute.For<IGitLightService>();
        gitService.GetRepositoriesAsync(7, Arg.Any<CancellationToken>()).Returns(
            [new GitLightRepoDto { Id = 1, ProjectId = 7, Slug = "w6", DefaultBranch = Branch }]);
        gitService.ResolveDiskPath(7, "w6").Returns(_bare);
        var sender = Substitute.For<IEmailSenderConfigurationResolver>();
        sender.ResolveAsync(Arg.Any<CancellationToken>()).Returns(new EmailSenderResolution());
        _git = new PipelineGitService(
            gitService, _cli, _repo, Substitute.For<IGitCommitRecorder>(), sender,
            NullLogger<PipelineGitService>.Instance);

        var organizations = Substitute.For<IOrganizationRepository>();
        organizations.GetDefaultOrganizationIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _service = new PipelineService(
            _repo, _git, Substitute.For<IAuditService>(), NullLogger<PipelineService>.Instance,
            Substitute.For<IEntityChangeNotifier>(), Substitute.For<IHttpContextAccessor>(), TimeProvider.System,
            new PipelineTemplateResolver(_repo), organizations);
    }

    private async Task SeedRepositoryAsync(string path, string yaml)
    {
        var (ok, _, error) = await _cli.CommitFileAsync(
            _bare, Branch, path, yaml, "seed", "someone", "someone@test", TestContext.Current.CancellationToken);
        Assert.True(ok, error);
    }

    private async Task<string?> RepositoryFileAsync(string path) =>
        (await _cli.GetBlobAsync(_bare, Branch, path, ct: TestContext.Current.CancellationToken))?.Content;

    private async Task<int> CommitCountAsync() =>
        (await _cli.GetCommitsAsync(_bare, Branch, skip: 0, take: 100, ct: TestContext.Current.CancellationToken)).Count;

    private static CreatePipelineRequest CreateRequest(string yaml) => new()
    {
        Name = "deploy",
        ProjectId = 7,
        SourceBranch = Branch,
        YamlDefinition = yaml
    };

    // --- CreatePipelineAsync (PipelineAuthoritativeGitCoordinator.CommitCreateAsync) ---

    [Fact]
    public async Task Create_OverADifferentFileAtTheSamePath_IsRefusedAndNothingIsWritten()
    {
        await SeedRepositoryAsync(".pipeline/deploy.yaml", RepositoryYaml);
        var before = await CommitCountAsync();

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            _service.CreatePipelineAsync(CreateRequest(AetheusYaml), TestContext.Current.CancellationToken));

        Assert.Contains("already holds", exception.Message);
        Assert.Equal(RepositoryYaml, await RepositoryFileAsync(".pipeline/deploy.yaml"));
        Assert.Equal(before, await CommitCountAsync());
        await _repo.DidNotReceive().AddPipelineAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithoutAFileAtThePath_CommitsTheDefinitionAsBefore()
    {
        await SeedRepositoryAsync(".pipeline/other.yaml", "name: other\n");
        var before = await CommitCountAsync();

        await _service.CreatePipelineAsync(CreateRequest(AetheusYaml), TestContext.Current.CancellationToken);

        Assert.Equal(AetheusYaml, await RepositoryFileAsync(".pipeline/deploy.yaml"));
        Assert.Equal(before + 1, await CommitCountAsync());
        await _repo.Received(1).AddPipelineAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithTheSameFileAlreadyInTheRepository_IsANoOpOnTheRepositoryAndStillCreates()
    {
        await SeedRepositoryAsync(".pipeline/deploy.yaml", AetheusYaml);
        var before = await CommitCountAsync();

        // Same content, other line endings and no trailing newline: carries no meaning for a definition.
        await _service.CreatePipelineAsync(
            CreateRequest(AetheusYaml.Replace("\n", "\r\n", StringComparison.Ordinal).TrimEnd()),
            TestContext.Current.CancellationToken);

        Assert.Equal(AetheusYaml, await RepositoryFileAsync(".pipeline/deploy.yaml"));
        Assert.Equal(before, await CommitCountAsync());
        await _repo.Received(1).AddPipelineAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>());
    }

    // --- CopyEnvironmentPipelinesToProjectAsync ---

    private void EnvironmentOwns(params (string Name, string Yaml)[] pipelines) =>
        _repo.GetPipelinesByEnvironmentAsync(5, Arg.Any<CancellationToken>()).Returns(
            pipelines.Select((p, index) => new Pipeline { Id = index + 1, Name = p.Name, YamlDefinition = p.Yaml }).ToList());

    [Fact]
    public async Task Copy_OverADifferentFileAtTheSamePath_KeepsTheRepositoryFileAndWritesTheOthers()
    {
        await SeedRepositoryAsync(".pipeline/staging-deploy.yaml", RepositoryYaml);
        EnvironmentOwns(("deploy", AetheusYaml), ("build", "name: build\n"));
        var before = await CommitCountAsync();

        var written = await _git.CopyEnvironmentPipelinesToProjectAsync(
            5, "staging", 7, "admin", TestContext.Current.CancellationToken);

        Assert.Equal(1, written);
        Assert.Equal(RepositoryYaml, await RepositoryFileAsync(".pipeline/staging-deploy.yaml"));
        Assert.Equal("name: build\n", await RepositoryFileAsync(".pipeline/staging-build.yaml"));
        Assert.Equal(before + 1, await CommitCountAsync());
    }

    [Fact]
    public async Task Copy_WhenEveryFileDiffers_CommitsNothing()
    {
        await SeedRepositoryAsync(".pipeline/staging-deploy.yaml", RepositoryYaml);
        EnvironmentOwns(("deploy", AetheusYaml));
        var before = await CommitCountAsync();

        var written = await _git.CopyEnvironmentPipelinesToProjectAsync(
            5, "staging", 7, "admin", TestContext.Current.CancellationToken);

        Assert.Equal(0, written);
        Assert.Equal(RepositoryYaml, await RepositoryFileAsync(".pipeline/staging-deploy.yaml"));
        Assert.Equal(before, await CommitCountAsync());
    }

    [Fact]
    public async Task Copy_WithoutFilesAtThePaths_CreatesThemAsBefore()
    {
        await SeedRepositoryAsync(".pipeline/other.yaml", "name: other\n");
        EnvironmentOwns(("deploy", AetheusYaml), ("build", "name: build\n"));
        var before = await CommitCountAsync();

        var written = await _git.CopyEnvironmentPipelinesToProjectAsync(
            5, "staging", 7, "admin", TestContext.Current.CancellationToken);

        Assert.Equal(2, written);
        Assert.Equal(AetheusYaml, await RepositoryFileAsync(".pipeline/staging-deploy.yaml"));
        Assert.Equal("name: build\n", await RepositoryFileAsync(".pipeline/staging-build.yaml"));
        Assert.Equal(before + 1, await CommitCountAsync());
    }

    [Fact]
    public async Task Copy_OfFilesAlreadyIdenticalInTheRepository_IsANoOp()
    {
        await SeedRepositoryAsync(".pipeline/staging-deploy.yaml", AetheusYaml);
        EnvironmentOwns(("deploy", AetheusYaml.Replace("\n", "\r\n", StringComparison.Ordinal)));
        var before = await CommitCountAsync();

        var written = await _git.CopyEnvironmentPipelinesToProjectAsync(
            5, "staging", 7, "admin", TestContext.Current.CancellationToken);

        Assert.Equal(0, written);
        Assert.Equal(AetheusYaml, await RepositoryFileAsync(".pipeline/staging-deploy.yaml"));
        Assert.Equal(before, await CommitCountAsync());
    }

    private static void RunGit(string args)
    {
        var git = ExecutableLocator.Require("git");
        var psi = new ProcessStartInfo(git, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{git}'.");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed ({process.ExitCode}): {stderr}");
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_bare)) return;
            foreach (var file in Directory.EnumerateFiles(_bare, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(_bare, true);
        }
        catch (IOException) { /* a temporary directory the OS cleans up */ }
    }
}
