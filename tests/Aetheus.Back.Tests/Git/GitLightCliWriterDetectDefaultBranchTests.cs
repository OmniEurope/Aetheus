// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Git;

/// <summary>
/// The default branch is read from the bare repository's HEAD file without starting git. The fixtures
/// are hand-written directories that git itself would refuse (no objects, no config): getting a branch
/// back therefore proves that no git process ran. Every read goes to the file, so a changed HEAD or a
/// new repository is seen at once.
/// </summary>
public sealed class GitLightCliWriterDetectDefaultBranchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aetheus-detect-head-{Guid.NewGuid():N}");
    private readonly GitLightCliWriter _writer = new(
        new GitProcessRunner(NullLogger<GitProcessRunner>.Instance), NullLogger<GitLightCliWriter>.Instance);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string FakeRepo(string name, string head, params string[] looseBranches)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, "refs", "heads"));
        File.WriteAllText(Path.Combine(path, "HEAD"), head + "\n");
        foreach (var branch in looseBranches)
        {
            var file = Path.Combine(path, "refs", "heads", branch);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "0123456789012345678901234567890123456789\n");
        }
        return path;
    }

    [Fact]
    public async Task FirstRead_ReturnsHeadBranch_WithoutGit()
    {
        var path = FakeRepo("first", "ref: refs/heads/main", "main", "develop");

        Assert.Equal("main", await _writer.DetectDefaultBranchAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnchangedRepository_SecondRead_ReturnsSameBranch_WithoutGit()
    {
        var path = FakeRepo("same", "ref: refs/heads/develop", "main", "develop");

        var first = await _writer.DetectDefaultBranchAsync(path, TestContext.Current.CancellationToken);
        var second = await _writer.DetectDefaultBranchAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("develop", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task HeadChange_IsPickedUp()
    {
        var path = FakeRepo("moved", "ref: refs/heads/main", "main", "develop");
        Assert.Equal("main", await _writer.DetectDefaultBranchAsync(path, TestContext.Current.CancellationToken));

        File.WriteAllText(Path.Combine(path, "HEAD"), "ref: refs/heads/develop\n");

        Assert.Equal("develop", await _writer.DetectDefaultBranchAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NewRepository_IsDetectedImmediately()
    {
        var first = FakeRepo("one", "ref: refs/heads/main", "main");
        Assert.Equal("main", await _writer.DetectDefaultBranchAsync(first, TestContext.Current.CancellationToken));

        var created = FakeRepo("two", "ref: refs/heads/trunk", "trunk");

        Assert.Equal("trunk", await _writer.DetectDefaultBranchAsync(created, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PackedBranch_IsRecognized_AndSlashedNamesKeepTheirPath()
    {
        var path = FakeRepo("packed", "ref: refs/heads/release/1.0");
        File.WriteAllText(Path.Combine(path, "packed-refs"),
            "# pack-refs with: peeled fully-peeled sorted\n0123456789012345678901234567890123456789 refs/heads/release/1.0\n");

        Assert.Equal("release/1.0", await _writer.DetectDefaultBranchAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HeadOnMissingBranch_FallsBackToGit_AndYieldsNullOnEmptyRepository()
    {
        var path = FakeRepo("dangling", "ref: refs/heads/gone");

        Assert.Null(await _writer.DetectDefaultBranchAsync(path, TestContext.Current.CancellationToken));
    }
}
