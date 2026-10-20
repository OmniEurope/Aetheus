// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The two directions of a container workspace on a platform that copies it: the prepared checkout brought
/// into the daemon-visible directory before a step, and the step's outputs published back after it. The
/// copy program is a real stand-in run as <c>&lt;program&gt; -a &lt;entry&gt; &lt;destination&gt;</c> (the real
/// <c>cp -a</c> outside Windows, an <c>xcopy</c> script on Windows), so files really move; failing and hanging
/// stand-ins show how a copy failure, a silent failure and a cancellation are reported.
/// </summary>
public sealed class ContainerWorkspaceSynchronizationTests : IDisposable
{
    private const string ReadyMarker = ".aetheus-container-workspace-ready";
    private static int _nextKey = 910_000;

    private readonly string _root = Directory.CreateTempSubdirectory("aetheus-workspace-sync-").FullName;
    private readonly int _key = Interlocked.Increment(ref _nextKey);

    public void Dispose()
    {
        ContainerWorkspaceMaterializer.ForgetWorkspace(_key);
        Directory.Delete(_root, recursive: true);
    }

    private string Prepared => Path.Combine(_root, "prepared");
    private string Fallback => Path.Combine(_root, "cw");

    private WorkspaceSyncPlatform Copying() => new(false, StandInProgram.Write(_root, "copy",
        """
        if exist "%~2\*" (
          xcopy "%~2" "%~3\%~nx2\" /E /I /Q /Y /H >nul
        ) else (
          copy /Y "%~2" "%~3\" >nul
        )
        exit /b %errorlevel%
        """,
        "exec cp -a \"$2\" \"$3\""));

    private WorkspaceSyncPlatform Failing(string windowsBody, string unixBody) =>
        new(false, StandInProgram.Write(_root, "copy-fails", windowsBody, unixBody));

    private void Seed(string directory, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    private static string Read(string directory, string path) => File.ReadAllText(Path.Combine(directory, path));

    [Fact]
    public async Task OnWindows_OrWhenThePreparedCheckoutIsAlreadyTheDaemonDirectory_NothingIsCopied()
    {
        var onWindows = await ContainerWorkspaceMaterializer.MaterializeAsync(
            _key, Prepared, Fallback, NullLogger.Instance, new WorkspaceSyncPlatform(true, "absent-program"),
            TestContext.Current.CancellationToken);
        var samePath = await ContainerWorkspaceMaterializer.MaterializeAsync(
            _key, Fallback, Fallback, NullLogger.Instance, new WorkspaceSyncPlatform(false, "absent-program"),
            TestContext.Current.CancellationToken);
        var publishedOnWindows = await ContainerWorkspaceMaterializer.SynchronizeBackAsync(
            _key, Fallback, Prepared, new WorkspaceSyncPlatform(true, "absent-program"),
            TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceMaterialization.Success(Prepared), onWindows);
        Assert.Equal(WorkspaceMaterialization.Success(Fallback), samePath);
        Assert.Equal(WorkspaceMaterialization.Success(Prepared), publishedOnWindows);
        Assert.False(Directory.Exists(Fallback));
    }

    [Fact]
    public async Task AMissingPreparedCheckout_FailsTheMaterialization()
    {
        var result = await ContainerWorkspaceMaterializer.MaterializeAsync(
            _key, Prepared, Fallback, NullLogger.Instance, Copying(), TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceMaterialization.Failure(Fallback, $"Prepared workspace '{Prepared}' is missing."), result);
    }

    [Fact]
    public async Task TheFirstMaterialization_ReplacesWhateverWasThere_CopiesTheCheckoutWithItsGitMetadata_AndMarksItReady()
    {
        Seed(Prepared, ("a.txt", "a"), ("src/b.txt", "b"), (".git/HEAD", "ref: main"));
        Seed(Fallback, ("leftover.txt", "from an unfinished attempt"));

        var result = await ContainerWorkspaceMaterializer.MaterializeAsync(
            _key, Prepared, Fallback, NullLogger.Instance, Copying(), TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceMaterialization.Success(Fallback), result);
        Assert.Equal("a", Read(Fallback, "a.txt"));
        Assert.Equal("b", Read(Fallback, "src/b.txt"));
        Assert.Equal("ref: main", Read(Fallback, ".git/HEAD"));
        Assert.Equal(_key.ToString(System.Globalization.CultureInfo.InvariantCulture), Read(Fallback, ReadyMarker));
        Assert.False(File.Exists(Path.Combine(Fallback, "leftover.txt")));
    }

    [Fact]
    public async Task ARefresh_CopiesTheCheckoutAgain_DropsWhatItNoLongerHolds_AndKeepsTheMaterializedGitMetadata()
    {
        Seed(Prepared, ("a.txt", "changed by a host stage"), (".git/HEAD", "moved"));
        Seed(Fallback, ("a.txt", "old"), ("gone.txt", "removed from the checkout"), (".git/HEAD", "pinned"), (ReadyMarker, "1"));

        var result = await ContainerWorkspaceMaterializer.MaterializeAsync(
            _key, Prepared, Fallback, NullLogger.Instance, Copying(), TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceMaterialization.Success(Fallback), result);
        Assert.Equal("changed by a host stage", Read(Fallback, "a.txt"));
        Assert.False(File.Exists(Path.Combine(Fallback, "gone.txt")));
        Assert.Equal("pinned", Read(Fallback, ".git/HEAD"));
    }

    [Fact]
    public async Task PublishingBack_CopiesTheOutputs_WithoutTheGitMetadataOrTheReadyMarker()
    {
        Seed(Fallback, ("bin/app.dll", "built"), (".git/HEAD", "container copy"), (ReadyMarker, "1"));
        Seed(Prepared, ("stale.txt", "deleted by the step"), (".git/HEAD", "host"));

        var result = await ContainerWorkspaceMaterializer.SynchronizeBackAsync(
            _key, Fallback, Prepared, Copying(), TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceMaterialization.Success(Prepared), result);
        Assert.Equal("built", Read(Prepared, "bin/app.dll"));
        Assert.Equal("host", Read(Prepared, ".git/HEAD"));
        Assert.False(File.Exists(Path.Combine(Prepared, ReadyMarker)));
        Assert.False(File.Exists(Path.Combine(Prepared, "stale.txt")));
    }

    [Fact]
    public async Task PublishingBack_FromAMissingContainerWorkspace_Fails()
    {
        var result = await ContainerWorkspaceMaterializer.SynchronizeBackAsync(
            _key, Fallback, Prepared, Copying(), TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceMaterialization.Failure(Prepared, $"Container workspace '{Fallback}' is missing."), result);
    }

    [Theory]
    [InlineData(true, "copy refused")]
    [InlineData(false, "cp exited with code 3.")]
    public async Task AFailedCopy_FailsBothDirections_WithItsErrorOrItsExitCode(bool writesAnError, string detail)
    {
        Seed(Prepared, ("a.txt", "a"));
        var container = Path.Combine(_root, "container");
        Seed(container, ("out.txt", "o"));
        var platform = writesAnError
            ? Failing("echo copy refused 1>&2\r\nexit /b 3", "echo copy refused >&2\nexit 3")
            : Failing("exit /b 3", "exit 3");

        var materialized = await ContainerWorkspaceMaterializer.MaterializeAsync(
            _key, Prepared, Fallback, NullLogger.Instance, platform, TestContext.Current.CancellationToken);
        var published = await ContainerWorkspaceMaterializer.SynchronizeBackAsync(
            _key, container, Prepared, platform, TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceMaterialization.Failure(Fallback, $"Could not materialize the prepared workspace: {detail}"), materialized);
        Assert.Equal(WorkspaceMaterialization.Failure(Prepared, $"Could not publish container outputs: {detail}"), published);
        Assert.False(File.Exists(Path.Combine(Fallback, ReadyMarker)));
    }

    [Fact]
    public async Task ACopyThatOutlivesItsBudget_IsKilled_AndReportedAsCancelled()
    {
        Seed(Prepared, ("a.txt", "a"));
        var platform = Failing("ping -n 60 127.0.0.1 >nul", "sleep 60");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(500));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await ContainerWorkspaceMaterializer.MaterializeAsync(
            _key, Prepared, Fallback, NullLogger.Instance, platform, budget.Token);

        Assert.Equal(
            WorkspaceMaterialization.Failure(Fallback, "Could not materialize the prepared workspace: Workspace synchronization was cancelled or timed out."),
            result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"The copy was not killed: {watch.Elapsed}.");
    }

    [Fact]
    public async Task ADestinationThatIsAFile_IsReportedInsteadOfThrown()
    {
        Seed(Prepared, ("a.txt", "a"));
        await File.WriteAllTextAsync(Fallback, "not a directory", TestContext.Current.CancellationToken);
        var container = Path.Combine(_root, "container");
        Seed(container, ("out.txt", "o"));
        var preparedFile = Path.Combine(_root, "prepared-file");
        await File.WriteAllTextAsync(preparedFile, "not a directory", TestContext.Current.CancellationToken);

        var materialized = await ContainerWorkspaceMaterializer.MaterializeAsync(
            _key, Prepared, Fallback, NullLogger.Instance, Copying(), TestContext.Current.CancellationToken);
        var published = await ContainerWorkspaceMaterializer.SynchronizeBackAsync(
            _key, container, preparedFile, Copying(), TestContext.Current.CancellationToken);

        Assert.False(materialized.IsSuccess);
        Assert.StartsWith("Could not materialize the prepared workspace: ", materialized.FailureReason, StringComparison.Ordinal);
        Assert.False(published.IsSuccess);
        Assert.StartsWith("Could not publish container outputs: ", published.FailureReason, StringComparison.Ordinal);
    }
}
