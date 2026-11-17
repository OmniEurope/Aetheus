// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-11 (decision of 2026-11-10, option A): pipelines present in an external repository's mirror and absent from the
/// database are imported read-only. The mirror's Git reads are doubles (the files, the head commit); the repository is
/// the real EF one on an in-memory database; the strict YAML validation is a double that returns the parsed
/// definition, so the import policy is what these tests exercise.
/// </summary>
public sealed class PipelineExternalImportTests : IDisposable
{
    private const int ProjectId = 7;
    private const int MirrorId = 3;
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
    private const string Moved = "89abcdef0123456789abcdef0123456789abcdef";

    // Normalized to LF: the cases edit it with "\n" fragments, and a Windows checkout (core.autocrlf) stores this
    // raw literal with CRLF line endings.
    private static readonly string CleanYaml = CleanYamlSource.ReplaceLineEndings("\n");

    private const string CleanYamlSource = """
        name: oe1-ci
        trigger: webhook
        schedule: "0 3 * * *"
        branches: [main]
        stages:
          - name: Build
            execution_role: build
            steps:
              - name: Compile
                shell: dotnet build
              - name: Test
                type: dotnet-test
        """;

    private readonly string _diskPath = Directory.CreateTempSubdirectory("aetheus-r11-").FullName;
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IGitLightCliService _cli = Substitute.For<IGitLightCliService>();
    private readonly IPipelineService _pipelineService = Substitute.For<IPipelineService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly AppDbContext _db;
    private readonly PipelineExternalImportService _sut;
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private string _head = Head;

    public PipelineExternalImportTests()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, clock);
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(_ =>
        [
            new GitLightRepoDto { Id = MirrorId, ProjectId = ProjectId, Name = "oe1", Slug = "oe1", DefaultBranch = "main", IsExternalMirror = true },
            new GitLightRepoDto { Id = 4, ProjectId = ProjectId, Name = "oe1-config", Slug = "oe1-config", DefaultBranch = "main" }
        ]);
        _git.ResolveDiskPath(ProjectId, "oe1").Returns(_diskPath);
        _cli.GetCommitsAsync(_diskPath, "main", 0, 1, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<GitCommitLogFilter?>())
            .Returns(_ => [new GitLightCommitDto { Sha = _head }]);
        _cli.GetTreeAsync(_diskPath, Arg.Any<string>(), ".pipeline", Arg.Any<CancellationToken>())
            .Returns(_ => _files.Keys.Select(path => new GitLightTreeEntryDto
            {
                Name = path[".pipeline/".Length..],
                Path = path,
                Type = GitTreeEntryType.Blob
            }).ToList());
        _cli.GetBlobAsync(_diskPath, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _files.TryGetValue(call.ArgAt<string>(2), out var yaml)
                ? new GitLightBlobDto { Path = call.ArgAt<string>(2), Content = yaml, Size = yaml.Length }
                : null);
        _pipelineService.ValidateYamlStrictAsync(
                Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var definition = YamlParsingHelper.ParseAndValidate(call.ArgAt<string>(0));
                return definition is null
                    ? new YamlValidationResultDto { IsValid = false, Errors = ["unreadable"] }
                    : new YamlValidationResultDto { IsValid = true, Definition = definition };
            });
        _sut = new PipelineExternalImportService(
            new PipelineExternalMirrorReader(_git, _cli, _pipelineService, new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            new PipelineExternalImportRepository(_db),
            Substitute.For<IPipelineRepository>(),
            _audit,
            Substitute.For<IEntityChangeNotifier>(),
            clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_diskPath, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ImportExternalPipelineRequest Request(string file = ".pipeline/oe1-ci.yaml", string? commit = null) =>
        new() { RepositoryId = MirrorId, FilePath = file, CommitSha = commit ?? _head };

    [Fact]
    public async Task List_ShowsAFileOfTheMirrorAbsentFromTheDatabaseAsAvailable()
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml;

        var result = await _sut.ListAsync(ProjectId, Ct);

        Assert.True(result.HasExternalRepository);
        var item = Assert.Single(result.Items);
        Assert.Equal(ExternalPipelineImportState.Available, item.State);
        Assert.Equal("oe1-ci", item.Name);
        Assert.Equal(Head, item.CommitSha);
        Assert.Empty(item.Problems);
    }

    [Fact]
    public async Task List_ProjectWithoutAnExternalMirror_HasNothingToShow()
    {
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([new GitLightRepoDto { Id = 4, ProjectId = ProjectId, Name = "own", Slug = "own" }]);

        var result = await _sut.ListAsync(ProjectId, Ct);

        Assert.False(result.HasExternalRepository);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Import_StoresTheReviewedFileReadOnlyAndManual_WhateverTriggerTheFileDeclares()
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml;

        var dto = await _sut.ImportAsync(ProjectId, Request(), "alice", Ct);

        var stored = await _db.Pipelines.SingleAsync(Ct);
        Assert.Equal(dto.Id, stored.Id);
        Assert.Equal(PipelineTriggerType.Manual, stored.TriggerType);
        Assert.Equal(CleanYaml, stored.YamlDefinition);
        Assert.Equal(MirrorId, stored.SourceRepositoryId);
        Assert.Equal(".pipeline/oe1-ci.yaml", stored.ExternalImportPath);
        Assert.Equal(Head, stored.ExternalImportCommit);
        Assert.Equal(PipelineRepositoryDefinitionGuard.Sha256(CleanYaml), stored.ExternalImportSha256);
        Assert.Equal("alice", stored.ExternalImportedBy);
        Assert.True(stored.IsExternalImport);
        Assert.Equal(".pipeline/oe1-ci.yaml", dto.ExternalImportPath);
        // Never written back: no commit of any kind was asked of the mirror.
        Assert.DoesNotContain(_cli.ReceivedCalls(), call => call.GetMethodInfo().Name.StartsWith("Commit", StringComparison.Ordinal)
                                                            || call.GetMethodInfo().Name.StartsWith("Merge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Import_SameContentAgain_ChangesNothing()
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml;
        var first = await _sut.ImportAsync(ProjectId, Request(), "alice", Ct);

        _head = Moved; // a new commit that does not touch the file
        var second = await _sut.ImportAsync(ProjectId, Request(), "bob", Ct);

        Assert.Equal(first.Id, second.Id);
        var stored = await _db.Pipelines.SingleAsync(Ct);
        Assert.Equal(Head, stored.ExternalImportCommit);
        Assert.Equal("alice", stored.ExternalImportedBy);
        await _audit.Received(1).LogAsync(Arg.Any<string>(), "Pipeline", Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Equal(ExternalPipelineImportState.Imported, Assert.Single((await _sut.ListAsync(ProjectId, Ct)).Items).State);
    }

    [Fact]
    public async Task Import_FileChangedInTheMirror_IsShownChangedAndTakenOnlyByANewImport()
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml;
        await _sut.ImportAsync(ProjectId, Request(), "alice", Ct);

        _head = Moved;
        var changedYaml = CleanYaml.Replace("dotnet build", "dotnet build -c Release", StringComparison.Ordinal);
        _files[".pipeline/oe1-ci.yaml"] = changedYaml;

        var listed = Assert.Single((await _sut.ListAsync(ProjectId, Ct)).Items);
        Assert.Equal(ExternalPipelineImportState.ChangedSinceImport, listed.State);
        Assert.Equal(Head, listed.ImportedCommitSha);
        Assert.Equal(CleanYaml, (await _db.Pipelines.AsNoTracking().SingleAsync(Ct)).YamlDefinition);

        await _sut.ImportAsync(ProjectId, Request(), "alice", Ct);

        var stored = await _db.Pipelines.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(changedYaml, stored.YamlDefinition);
        Assert.Equal(Moved, stored.ExternalImportCommit);
        Assert.Equal(PipelineTriggerType.Manual, stored.TriggerType);
    }

    [Theory]
    [InlineData("vaults: [prod-secrets]\n", "vaults")]
    [InlineData("variable_libraries: [oe1.share]\n", "variable_libraries")]
    [InlineData("requires:\n  environments: [Production]\n", "requires")]
    [InlineData("on_success:\n  - pipeline: oe1-deploy-prod\n", "on_success")]
    [InlineData("source:\n  repository: oe1-config\n", "source")]
    [InlineData("requires:\n  capabilities: [docker]\n", "requires")]
    [InlineData("release_number: allocate\n", "release_number")]
    [InlineData("observed_port_policy: ignore\n", "observed_port_policy")]
    [InlineData("isolation:\n  mode: container\n  toolchain: dotnet\n", "isolation")]
    public async Task Import_DefinitionAskingForSecretsOrOtherPipelines_IsRefusedAndNothingIsStored(string header, string key)
    {
        _files[".pipeline/oe1-ci.yaml"] = header + CleanYaml;

        var listed = Assert.Single((await _sut.ListAsync(ProjectId, Ct)).Items);
        var refusal = await Assert.ThrowsAsync<BadRequestException>(() => _sut.ImportAsync(ProjectId, Request(), "alice", Ct));

        Assert.Equal(ExternalPipelineImportState.Refused, listed.State);
        Assert.Contains(listed.Problems, problem => problem.Contains(key, StringComparison.Ordinal));
        Assert.Contains(key, refusal.Message, StringComparison.Ordinal);
        Assert.Empty(_db.Pipelines);
    }

    [Theory]
    [InlineData("    environment: Production\n", "environment")]
    [InlineData("    pool: prod-pool\n", "pool")]
    [InlineData("    execution_role: deploy\n", "execution_role")]
    [InlineData("    agent: prod-web-01\n", "(agent)")]
    [InlineData("    os: windows\n", "(os)")]
    [InlineData("    group: nightly\n", "(group)")]
    [InlineData("    matrix:\n      dotnet: [\"8\", \"9\"]\n", "(matrix)")]
    [InlineData("    isolation:\n      mode: container\n      image: docker.io/library/alpine@sha256:0000000000000000000000000000000000000000000000000000000000000000\n      network: bridge\n", "(isolation)")]
    [InlineData("    isolation:\n      mode: process\n      runtime: runc\n", "(isolation)")]
    [InlineData("    artifacts: [\"bin/**\"]\n", "(artifacts)")]
    [InlineData("    artifact_name: oe1-build\n", "(artifact_name)")]
    public async Task Import_StageTargetingADeploymentOrAPool_IsRefused(string stageLine, string key)
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml.Replace("    execution_role: build\n", stageLine, StringComparison.Ordinal);

        var refusal = await Assert.ThrowsAsync<BadRequestException>(() => _sut.ImportAsync(ProjectId, Request(), "alice", Ct));

        Assert.Contains(key, refusal.Message, StringComparison.Ordinal);
        Assert.Empty(_db.Pipelines);
    }

    [Theory]
    [InlineData("deploy")]
    [InlineData("trigger")]
    [InlineData("advance-branch")]
    [InlineData("ai")]
    [InlineData("aetheus")]
    [InlineData("bluegreen-switch")]
    [InlineData("artifacts")]
    [InlineData("restore-artifacts")]
    [InlineData("release")]
    public async Task Import_StepTypeOutsideBuildAndAnalysis_IsRefused(string type)
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml.Replace("type: dotnet-test", $"type: {type}", StringComparison.Ordinal);

        var refusal = await Assert.ThrowsAsync<BadRequestException>(() => _sut.ImportAsync(ProjectId, Request(), "alice", Ct));

        Assert.Contains($"'{type}'", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(_db.Pipelines);
    }

    [Theory]
    [InlineData("target_url: https://qa.example.test\n        target_classification: qa", "target_url")]
    [InlineData("api_specification_url: https://qa.example.test/openapi.json", "api_specification_url")]
    [InlineData("api_specification_urls: [\"https://qa.example.test/openapi.json\"]", "api_specification_urls")]
    [InlineData("origin: https://prod.example.test", "origin")]
    public async Task Import_ScannerAimedAtANetworkTarget_IsRefused(string field, string key)
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml.Replace(
            "type: dotnet-test", "type: scanner\n        scanner: zap\n        " + field, StringComparison.Ordinal);

        var refusal = await Assert.ThrowsAsync<BadRequestException>(() => _sut.ImportAsync(ProjectId, Request(), "alice", Ct));

        Assert.Contains(key, refusal.Message, StringComparison.Ordinal);
        Assert.Empty(_db.Pipelines);
    }

    [Fact]
    public void Policy_StrategyOrAJobChoosingItsRunner_IsRefused()
    {
        var definition = new PipelineYamlDefinition
        {
            Name = "oe1-ci",
            Stages =
            [
                new PipelineStageDefinition { Name = "Build", Strategy = new PipelineDeploymentStrategy() },
                new PipelineStageDefinition
                {
                    Name = "Test",
                    Jobs = [new PipelineJobDefinition { Name = "unit", Agent = "runner-b", Os = "linux", Artifacts = ["out/**"] }]
                }
            ]
        };

        var problems = PipelineExternalImportPolicy.Validate(definition);

        Assert.Contains(problems, problem => problem.Contains("(strategy)", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("job 'unit'", StringComparison.Ordinal) && problem.Contains("(agent)", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("job 'unit'", StringComparison.Ordinal) && problem.Contains("(os)", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("job 'unit'", StringComparison.Ordinal) && problem.Contains("(artifacts)", StringComparison.Ordinal));
    }

    [Fact]
    public void Policy_BuildStageOnTheDefaultAgent_IsAllowed()
    {
        var definition = YamlParsingHelper.ParseAndValidate(
            CleanYaml.Replace("    execution_role: build\n", "    execution_role: build\n    agent: default\n", StringComparison.Ordinal));

        Assert.NotNull(definition);
        Assert.Empty(PipelineExternalImportPolicy.Validate(definition));
    }

    [Fact]
    public async Task ImportedPipeline_NeverFiresAutomatically_EvenWithAWebhookOrScheduleTriggerColumn()
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml;
        await _sut.ImportAsync(ProjectId, Request(), "alice", Ct);
        var stored = await _db.Pipelines.SingleAsync(Ct);
        var triggers = new PipelineAutoTriggerRepository(_db);

        stored.TriggerType = PipelineTriggerType.Schedule;
        await _db.SaveChangesAsync(Ct);
        Assert.Empty(await triggers.GetScheduledPipelinesAsync(Ct));
        Assert.Equal(default, await triggers.GetScheduledPipelinesFingerprintAsync(Ct));

        stored.TriggerType = PipelineTriggerType.Webhook;
        await _db.SaveChangesAsync(Ct);
        Assert.Empty(await triggers.GetWebhookTriggeredPipelinesForProjectAsync(ProjectId, Ct));
        Assert.Empty(await triggers.GetWebhookTriggeredPipelinesWithProjectAsync(Ct));
    }

    [Fact]
    public async Task Import_UnreadableOrOversizedFile_IsRefused()
    {
        _files[".pipeline/broken.yaml"] = "name: broken\nstages: [\n";
        _files[".pipeline/huge.yaml"] = "name: huge\n# " + new string('x', PipelineDefinitionLimits.MaxYamlLength) + "\n" + CleanYaml;

        var items = (await _sut.ListAsync(ProjectId, Ct)).Items;

        Assert.All(items, item => Assert.Equal(ExternalPipelineImportState.Refused, item.State));
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ImportAsync(ProjectId, Request(".pipeline/broken.yaml"), "alice", Ct));
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ImportAsync(ProjectId, Request(".pipeline/huge.yaml"), "alice", Ct));
        Assert.Empty(_db.Pipelines);
    }

    [Fact]
    public async Task Import_MirrorMovedSinceTheReview_IsAConflict()
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml;
        _head = Moved;

        await Assert.ThrowsAsync<ConflictException>(() => _sut.ImportAsync(ProjectId, Request(commit: Head), "alice", Ct));
        Assert.Empty(_db.Pipelines);
    }

    [Fact]
    public async Task Import_NeverReplacesAPipelineDefinedInAetheus()
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml;
        _db.Pipelines.Add(new Pipeline { Name = "oe1-ci", ProjectId = ProjectId, YamlDefinition = "name: oe1-ci\nstages: []" });
        await _db.SaveChangesAsync(Ct);

        var listed = Assert.Single((await _sut.ListAsync(ProjectId, Ct)).Items);
        await Assert.ThrowsAsync<ConflictException>(() => _sut.ImportAsync(ProjectId, Request(), "alice", Ct));

        Assert.Equal(ExternalPipelineImportState.NameTaken, listed.State);
        Assert.Equal("name: oe1-ci\nstages: []", (await _db.Pipelines.AsNoTracking().SingleAsync(Ct)).YamlDefinition);
    }

    [Theory]
    [InlineData(4, ".pipeline/oe1-ci.yaml")]
    [InlineData(99, ".pipeline/oe1-ci.yaml")]
    public async Task Import_RepositoryThatIsNotAnExternalMirrorOfTheProject_IsNotFound(int repositoryId, string file)
    {
        _files[file] = CleanYaml;

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ImportAsync(
            ProjectId, new ImportExternalPipelineRequest { RepositoryId = repositoryId, FilePath = file, CommitSha = Head }, "alice", Ct));
    }

    [Theory]
    [InlineData(".pipeline/../secrets.yaml")]
    [InlineData(".pipeline/sub/ci.yaml")]
    [InlineData("deploy/ci.yaml")]
    [InlineData(".pipeline/ci.txt")]
    [InlineData(".pipeline/.hidden.yaml")]
    public async Task Import_PathOutsidePipelineDirectory_IsRefused(string path)
    {
        Assert.False(PipelineExternalImportPolicy.IsSafeFilePath(path));
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ImportAsync(ProjectId, Request(path), "alice", Ct));
    }

    [Fact]
    public async Task ImportedPipeline_CannotBeEditedInAetheus()
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml;
        await _sut.ImportAsync(ProjectId, Request(), "alice", Ct);
        var stored = await _db.Pipelines.SingleAsync(Ct);

        var refusal = Assert.Throws<ConflictException>(() => PipelineExternalImportPolicy.EnsureEditable(stored));

        Assert.Contains("read-only", refusal.Message, StringComparison.Ordinal);
        PipelineExternalImportPolicy.EnsureEditable(new Pipeline { Name = "own" });
    }
}
