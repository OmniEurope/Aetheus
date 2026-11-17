// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-11: the mirror's files are untrusted. A YAML alias bomb, a deep or huge file, a mirror with many files and two
/// imports of the same pipeline at once are all answered per file or with a 409, never with a 500 or unbounded work.
/// </summary>
public sealed class PipelineExternalImportHardeningTests : IDisposable
{
    private const int ProjectId = 7;
    private const int MirrorId = 3;
    private const string Head = "0123456789abcdef0123456789abcdef01234567";

    private const string CleanYaml = "name: oe1-ci\ntrigger: manual\nstages:\n  - name: Build\n    steps:\n      - name: Compile\n        shell: dotnet build\n";

    private readonly string _diskPath = Directory.CreateTempSubdirectory("aetheus-r11h-").FullName;
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IGitLightCliService _cli = Substitute.For<IGitLightCliService>();
    private readonly IPipelineService _pipelineService = Substitute.For<IPipelineService>();
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _treeSizes = new(StringComparer.Ordinal);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ThrowOnSaveInterceptor _interceptor = new();
    private readonly AppDbContext _db;
    private readonly PipelineExternalImportService _sut;
    private string _head = Head;
    private int _blobReads;

    public PipelineExternalImportHardeningTests()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).AddInterceptors(_interceptor).Options,
            _clock);
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(_ =>
            [new GitLightRepoDto { Id = MirrorId, ProjectId = ProjectId, Name = "oe1", Slug = "oe1", DefaultBranch = "main", IsExternalMirror = true }]);
        _git.ResolveDiskPath(ProjectId, "oe1").Returns(_diskPath);
        _cli.GetCommitsAsync(_diskPath, "main", 0, 1, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<GitCommitLogFilter?>())
            .Returns(_ => [new GitLightCommitDto { Sha = _head }]);
        _cli.GetTreeAsync(_diskPath, Arg.Any<string>(), ".pipeline", Arg.Any<CancellationToken>())
            .Returns(_ => _files.Keys.Select(path => new GitLightTreeEntryDto
            {
                Name = path[".pipeline/".Length..],
                Path = path,
                Type = GitTreeEntryType.Blob,
                Size = _treeSizes.TryGetValue(path, out var size) ? size : null
            }).ToList());
        _cli.GetBlobAsync(_diskPath, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _blobReads++;
                return _files.TryGetValue(call.ArgAt<string>(2), out var yaml)
                    ? new GitLightBlobDto { Path = call.ArgAt<string>(2), Content = yaml, Size = yaml.Length }
                    : null;
            });
        // The strict validation is the real YAML parse, so a file the guard let through is parsed for real.
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
            new PipelineExternalMirrorReader(_git, _cli, _pipelineService, _cache),
            new PipelineExternalImportRepository(_db),
            Substitute.For<IPipelineRepository>(),
            Substitute.For<IAuditService>(),
            Substitute.For<IEntityChangeNotifier>(),
            _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
        Directory.Delete(_diskPath, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ImportExternalPipelineRequest Request(string file = ".pipeline/oe1-ci.yaml") =>
        new() { RepositoryId = MirrorId, FilePath = file, CommitSha = _head };

    private static string BillionLaughs(int levels)
    {
        var yaml = new StringBuilder("name: bomb\nlol: &a0 [x, x, x, x, x, x, x, x, x]\n");
        for (var level = 1; level < levels; level++)
        {
            var previous = $"*a{level - 1}";
            yaml.Append(CultureInfo.InvariantCulture, $"l{level}: &a{level} [{string.Join(", ", Enumerable.Repeat(previous, 9))}]\n");
        }
        return yaml.ToString();
    }

    [Fact]
    public async Task List_BillionLaughsFile_IsRefusedPerFile_AndTheOtherFilesStayListed()
    {
        _files[".pipeline/bomb.yaml"] = BillionLaughs(9);
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml;

        var result = await _sut.ListAsync(ProjectId, Ct);

        var bomb = Assert.Single(result.Items, item => item.FilePath == ".pipeline/bomb.yaml");
        Assert.Equal(ExternalPipelineImportState.Refused, bomb.State);
        Assert.Contains(bomb.Problems, problem => problem.Contains("alias", StringComparison.Ordinal));
        Assert.Equal(ExternalPipelineImportState.Available, Assert.Single(result.Items, item => item.Name == "oe1-ci").State);
        // The bomb was refused before the strict validation (the real parse) ever saw it.
        await _pipelineService.DidNotReceive().ValidateYamlStrictAsync(
            Arg.Is<string>(yaml => yaml.Contains("bomb", StringComparison.Ordinal)),
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Import_BillionLaughsFile_IsABadRequest_NotAServerError()
    {
        _files[".pipeline/bomb.yaml"] = BillionLaughs(9);

        var refusal = await Assert.ThrowsAsync<BadRequestException>(() => _sut.ImportAsync(ProjectId, Request(".pipeline/bomb.yaml"), "alice", Ct));

        Assert.Contains("alias", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(_db.Pipelines);
    }

    [Fact]
    public void Guard_SmallAliasMergeKey_IsRefusedToo()
    {
        var refusal = PipelineExternalYamlGuard.Check("base: &b {a: 1}\nother:\n  <<: *b\n");

        Assert.NotNull(refusal);
        Assert.Contains("line 3", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_PlainDefinition_IsAccepted_AndAnAnchorWithoutAliasIsHarmless()
    {
        Assert.Null(PipelineExternalYamlGuard.Check(CleanYaml));
        Assert.Null(PipelineExternalYamlGuard.Check("name: &n ci\nstages: []\n"));
    }

    [Fact]
    public void Guard_TooManyNodes_IsRefused()
    {
        var yaml = "items: [" + string.Join(", ", Enumerable.Repeat("1", PipelineExternalYamlGuard.MaxNodes + 1)) + "]\n";

        var refusal = PipelineExternalYamlGuard.Check(yaml);

        Assert.NotNull(refusal);
        Assert.Contains("nodes", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_DeepNesting_IsRefusedWithoutRecursion()
    {
        // 150 000 levels would overflow the stack of a recursive reader; the guard counts events and stops at the bound.
        var yaml = new string('[', 150_000);

        var refusal = PipelineExternalYamlGuard.Check(yaml);

        Assert.NotNull(refusal);
        Assert.Contains("nested", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_MirrorWithMoreFilesThanTheCap_ReadsOnlyTheCapAndSaysSo()
    {
        for (var index = 0; index < PipelineExternalMirrorReader.MaxFiles + 7; index++)
            _files[$".pipeline/p{index:000}.yaml"] = CleanYaml.Replace("oe1-ci", $"p{index:000}", StringComparison.Ordinal);

        var result = await _sut.ListAsync(ProjectId, Ct);

        Assert.Equal(PipelineExternalMirrorReader.MaxFiles, result.Items.Count);
        Assert.Equal(PipelineExternalMirrorReader.MaxFiles, _blobReads);
        Assert.Equal("p000", result.Items[0].Name);
        var note = Assert.Single(result.UnreadableRepositories);
        Assert.Contains("7 more", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_FileLargerThanTheCapAccordingToTheTree_IsRefusedWithoutReadingIt()
    {
        _files[".pipeline/huge.yaml"] = "name: huge\n";
        _treeSizes[".pipeline/huge.yaml"] = PipelineDefinitionLimits.MaxYamlLength + 1;

        var item = Assert.Single((await _sut.ListAsync(ProjectId, Ct)).Items);

        Assert.Equal(ExternalPipelineImportState.Refused, item.State);
        Assert.Contains(item.Problems, problem => problem.Contains("exceeds", StringComparison.Ordinal));
        Assert.Equal(0, _blobReads);
    }

    [Fact]
    public async Task List_RepeatedAtTheSameCommit_ReadsTheMirrorOnce_AndANewCommitReadsAgain()
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml;

        await _sut.ListAsync(ProjectId, Ct);
        await _sut.ListAsync(ProjectId, Ct);
        Assert.Equal(1, _blobReads);

        _head = "89abcdef0123456789abcdef0123456789abcdef";
        await _sut.ListAsync(ProjectId, Ct);
        Assert.Equal(2, _blobReads);
    }

    [Fact]
    public async Task Import_NeverUsesTheListingCache()
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml;
        await _sut.ListAsync(ProjectId, Ct);

        await _sut.ImportAsync(ProjectId, Request(), "alice", Ct);

        Assert.Equal(2, _blobReads);
    }

    [Fact]
    public async Task Import_LosingTheRaceOnTheUniqueName_IsAConflictAndStoresNothing()
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml;
        _interceptor.Failure = new DbUpdateException(
            "duplicate key", new PostgresException("duplicate key value", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => _sut.ImportAsync(ProjectId, Request(), "alice", Ct));

        Assert.Contains("same time", conflict.Message, StringComparison.Ordinal);
        Assert.Empty(_db.ChangeTracker.Entries());
        _interceptor.Failure = null;
        // The loser can simply start again once the winner is stored.
        Assert.Equal("oe1-ci", (await _sut.ImportAsync(ProjectId, Request(), "alice", Ct)).Name);
    }

    [Fact]
    public async Task Import_OtherDatabaseFailure_IsNotTurnedIntoAConflict()
    {
        _files[".pipeline/oe1-ci.yaml"] = CleanYaml;
        _interceptor.Failure = new DbUpdateException("boom", new InvalidOperationException("disk full"));

        await Assert.ThrowsAsync<DbUpdateException>(() => _sut.ImportAsync(ProjectId, Request(), "alice", Ct));
    }

    private sealed class ThrowOnSaveInterceptor : SaveChangesInterceptor
    {
        public Exception? Failure { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => Failure is null ? base.SavingChangesAsync(eventData, result, cancellationToken) : throw Failure;
    }
}
