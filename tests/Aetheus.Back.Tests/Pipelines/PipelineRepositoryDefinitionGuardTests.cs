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
/// Recette R-95: saving a pipeline's settings (its source branch) committed Aetheus's copy of the
/// definition over the repository's file, dropping lines pushed by hand. These tests run the real
/// <see cref="PipelineService"/>, the real <see cref="PipelineGitService"/> and the real git writer
/// against a temporary bare repository; only the database and the repository lookup are doubles.
/// </summary>
public sealed class PipelineRepositoryDefinitionGuardTests : IDisposable
{
    private const string Branch = "develop";
    private const string FilePath = ".pipeline/deploy.yaml";

    private const string AetheusYaml = "name: deploy\ntrigger: manual\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: dotnet build\n";

    // What was pushed by hand on the branch: two variables Aetheus never saw.
    private const string RepositoryYaml = "name: deploy\ntrigger: manual\nvariables:\n  COMPOSE_FILE: deploy/compose.yml\n  DEPLOY_HAS_API: \"true\"\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: dotnet build\n";

    private readonly string _bare = Path.Combine(Path.GetTempPath(), $"pipeline-guard-{Guid.NewGuid():N}.git");
    private readonly GitLightCliService _cli;
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly PipelineService _sut;
    private readonly Pipeline _existing;

    public PipelineRepositoryDefinitionGuardTests()
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
        var pipelineGit = new PipelineGitService(
            gitService, _cli, _repo, Substitute.For<IGitCommitRecorder>(), sender,
            NullLogger<PipelineGitService>.Instance);

        var organizations = Substitute.For<IOrganizationRepository>();
        organizations.GetDefaultOrganizationIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _existing = new Pipeline
        {
            Id = 1,
            Name = "deploy",
            ProjectId = 7,
            SourceBranch = null,
            YamlDefinition = AetheusYaml,
            Runs = []
        };
        _repo.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(_existing);
        _repo.GetPipelineSourceFieldsAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineSourceFields(7, "deploy", null, null));
        _sut = new PipelineService(
            _repo, pipelineGit, Substitute.For<IAuditService>(), NullLogger<PipelineService>.Instance,
            Substitute.For<IEntityChangeNotifier>(), Substitute.For<IHttpContextAccessor>(), TimeProvider.System,
            new PipelineTemplateResolver(_repo), organizations);
    }

    private async Task SeedRepositoryAsync(string yaml)
    {
        var (ok, _, error) = await _cli.CommitFileAsync(
            _bare, Branch, FilePath, yaml, "seed", "someone", "someone@test", TestContext.Current.CancellationToken);
        Assert.True(ok, error);
    }

    private async Task<string?> RepositoryFileAsync(string path = FilePath) =>
        (await _cli.GetBlobAsync(_bare, Branch, path, ct: TestContext.Current.CancellationToken))?.Content;

    private async Task<int> CommitCountAsync() =>
        (await _cli.GetCommitsAsync(_bare, Branch, skip: 0, take: 100, ct: TestContext.Current.CancellationToken)).Count;

    private UpdatePipelineRequest Request(string yaml, string name = "deploy", string? confirmedSha = null) => new()
    {
        Name = name,
        ProjectId = 7,
        SourceBranch = Branch,
        YamlDefinition = yaml,
        ConfirmedRepositoryYamlSha256 = confirmedSha
    };

    [Fact]
    public async Task R95_SavingOnlyTheSourceBranch_KeepsTheLinesPushedToTheRepository()
    {
        await SeedRepositoryAsync(RepositoryYaml);
        var before = await CommitCountAsync();

        // The Parameters tab sends back the definition it loaded from Aetheus (no edit) with the branch.
        await _sut.UpdatePipelineAsync(1, Request(AetheusYaml), TestContext.Current.CancellationToken);

        Assert.Equal(RepositoryYaml, await RepositoryFileAsync());
        Assert.Equal(before, await CommitCountAsync());
        Assert.Equal(RepositoryYaml, _existing.YamlDefinition);
        Assert.Equal(Branch, _existing.SourceBranch);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditedDefinition_OverAnUnseenRepositoryChange_IsRefusedAndNothingIsWritten()
    {
        await SeedRepositoryAsync(RepositoryYaml);
        var before = await CommitCountAsync();

        var exception = await Assert.ThrowsAsync<ConflictException>(() => _sut.UpdatePipelineAsync(
            1, Request(AetheusYaml.Replace("dotnet build", "dotnet build -c Release", StringComparison.Ordinal)),
            TestContext.Current.CancellationToken));

        Assert.Contains("changed in the repository", exception.Message);
        Assert.Equal(RepositoryYaml, await RepositoryFileAsync());
        Assert.Equal(before, await CommitCountAsync());
        Assert.Equal(AetheusYaml, _existing.YamlDefinition);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditedDefinition_OverAnUnseenRepositoryChange_IsCommittedOnceTheReviewedFileIsConfirmed()
    {
        await SeedRepositoryAsync(RepositoryYaml);
        var edited = AetheusYaml.Replace("dotnet build", "dotnet build -c Release", StringComparison.Ordinal);

        await _sut.UpdatePipelineAsync(
            1, Request(edited, confirmedSha: PipelineRepositoryDefinitionGuard.Sha256(RepositoryYaml)),
            TestContext.Current.CancellationToken);

        Assert.Equal(edited, await RepositoryFileAsync());
        Assert.Equal(edited, _existing.YamlDefinition);
    }

    [Fact]
    public async Task EditedDefinition_ConfirmedAgainstAnotherFile_IsStillRefused()
    {
        await SeedRepositoryAsync(RepositoryYaml);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.UpdatePipelineAsync(
            1, Request(AetheusYaml + "# edit\n", confirmedSha: PipelineRepositoryDefinitionGuard.Sha256(AetheusYaml)),
            TestContext.Current.CancellationToken));

        Assert.Equal(RepositoryYaml, await RepositoryFileAsync());
    }

    [Fact]
    public async Task EditedDefinition_WhenTheRepositoryHoldsWhatAetheusSynced_IsCommitted()
    {
        await SeedRepositoryAsync(AetheusYaml);
        var edited = AetheusYaml.Replace("dotnet build", "dotnet build -c Release", StringComparison.Ordinal);

        await _sut.UpdatePipelineAsync(1, Request(edited), TestContext.Current.CancellationToken);

        Assert.Equal(edited, await RepositoryFileAsync());
        Assert.Equal(edited, _existing.YamlDefinition);
    }

    [Fact]
    public async Task SavingWithoutAFileInTheRepository_StillCreatesIt()
    {
        await _sut.UpdatePipelineAsync(1, Request(AetheusYaml), TestContext.Current.CancellationToken);

        Assert.Equal(AetheusYaml, await RepositoryFileAsync());
    }

    [Fact]
    public async Task RenamingWithoutEditing_MovesTheRepositoryFileWithItsLines_NotAetheusCopy()
    {
        await SeedRepositoryAsync(RepositoryYaml);

        await _sut.UpdatePipelineAsync(1, Request(AetheusYaml, name: "deploy-api"), TestContext.Current.CancellationToken);

        Assert.Equal(RepositoryYaml, await RepositoryFileAsync(".pipeline/deploy-api.yaml"));
        Assert.Null(await RepositoryFileAsync());
        Assert.Equal(RepositoryYaml, _existing.YamlDefinition);
    }

    [Fact]
    public async Task TheReviewRead_ReturnsTheRepositoryFileAndItsHash()
    {
        await SeedRepositoryAsync(RepositoryYaml);

        var definition = await _sut.GetRepositoryDefinitionAsync(1, Branch, null, TestContext.Current.CancellationToken);

        Assert.True(definition!.Exists);
        Assert.Equal(RepositoryYaml, definition.Yaml);
        Assert.Equal(PipelineRepositoryDefinitionGuard.Sha256(RepositoryYaml), definition.Sha256);
    }

    [Fact]
    public async Task TheReviewRead_OfABranchWithoutTheFile_SaysItDoesNotExist()
    {
        await SeedRepositoryAsync(RepositoryYaml);

        var definition = await _sut.GetRepositoryDefinitionAsync(1, "release", null, TestContext.Current.CancellationToken);

        Assert.False(definition!.Exists);
        Assert.Null(definition.Yaml);
    }

    [Fact]
    public void TheHash_IgnoresLineEndingsAndTrailingBlankLines()
    {
        Assert.Equal(
            PipelineRepositoryDefinitionGuard.Sha256("a: 1\nb: 2\n"),
            PipelineRepositoryDefinitionGuard.Sha256("a: 1\r\nb: 2\r\n\r\n"));
        Assert.NotEqual(
            PipelineRepositoryDefinitionGuard.Sha256("a: 1\nb: 2\n"),
            PipelineRepositoryDefinitionGuard.Sha256("a: 1\nb: 3\n"));
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
