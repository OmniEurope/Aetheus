// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The <c>--verify</c> mode of <c>export-public-distribution.mjs</c> ported to <c>verify-public-distribution</c>,
/// run through the real executor on throwaway Git repositories: an internal one (the step's workspace, holding a
/// <c>boundary.json</c>) and a public checkout laid out as the distribution of one of its commits. The commits
/// are dated on purpose, one minute apart: the candidates are the commits made before the publication.
/// </summary>
public sealed class VerifyPublicDistributionFunctionTests : IDisposable
{
    private const string Prefix = "Function 'verify-public-distribution' failed: ";
    private readonly FunctionTestHarness _harness = new("aetheus-public-distribution");
    private readonly Repositories _repositories;

    public VerifyPublicDistributionFunctionTests() => _repositories = new Repositories(_harness);

    public void Dispose()
    {
        // Git writes its objects read-only, which Windows refuses to delete.
        foreach (var path in Directory.EnumerateFiles(_harness.Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            File.SetAttributes(path, FileAttributes.Normal);
        _harness.Dispose();
    }

    [Fact]
    public async Task APublicCheckoutEqualToTheHead_IsVerified_AndTheHeadIsTheMatchedCommit()
    {
        var head = _repositories.InternalCommit("one", ("src/app.txt", "v1\n"), ("README.md", "internal\n"), ("docs/guide.md", "private\n"));
        var published = _repositories.Publish(("src/app.txt", "v1\n"), ("README.md", "internal\n"));

        var (result, variables) = await RunAsync(new() { ["branch"] = "HEAD" });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("true", variables["PUBLIC_VERIFIED"]);
        Assert.Equal(head, variables["PUBLIC_MATCHED_COMMIT"]);
        Assert.Contains($"Verified: public commit {published} is the distribution of HEAD commit {head}.", Lines(TaskLogLevel.Info));
        Assert.DoesNotContain(_harness.Log, entry => entry.Line.StartsWith("Refused:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBranchDefaultsToMain_AndTheDepthTo20()
    {
        var head = _repositories.InternalCommit("one", ("src/app.txt", "v1\n"), ("README.md", "internal\n"));
        _repositories.Publish(("src/app.txt", "v1\n"), ("README.md", "internal\n"));

        var (_, variables) = await RunAsync(new());

        Assert.Equal("true", variables["PUBLIC_VERIFIED"]);
        Assert.Equal(head, variables["PUBLIC_MATCHED_COMMIT"]);
        Assert.Contains($"is the distribution of main commit {head}.", Lines(TaskLogLevel.Info).Single(line => line.StartsWith("Verified:", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APublicCheckoutThatDiffers_IsRefused_NamingTheClosestCommit_YetTheStepSucceeds()
    {
        var head = _repositories.InternalCommit("one", ("src/app.txt", "v1\n"), ("README.md", "internal\n"));
        var published = _repositories.Publish(("src/app.txt", "tampered\n"), ("README.md", "internal\n"), ("extra.sh", "e\n"));

        var (result, variables) = await RunAsync(new() { ["branch"] = "HEAD" });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("false", variables["PUBLIC_VERIFIED"]);
        Assert.Equal(string.Empty, variables["PUBLIC_MATCHED_COMMIT"]);
        Assert.Equal($"{head[..9]}: 2 difference(s)", Lines(TaskLogLevel.Info).Single(line => line.Contains(": 2 difference(s)", StringComparison.Ordinal)));
        Assert.Equal(
            [
                $"Refused: public commit {published} is the distribution of none of the last 1 commit(s) of HEAD before its publication ({_repositories.PublishedAt}).",
                $"Closest: {head}, 2 difference(s):",
                "  changed: src/app.txt",
                "  unexpected: extra.sh"
            ],
            Lines(TaskLogLevel.Warning));
        Assert.DoesNotContain(_harness.Log, entry => entry.Line.StartsWith("Verified:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACheckoutEqualToAnOlderCommitWithinTheDepth_IsVerified_WithThatCommit()
    {
        _repositories.InternalCommit("one", ("src/app.txt", "v1\n"), ("README.md", "internal\n"));
        var middle = _repositories.InternalCommit("two", ("src/app.txt", "v2\n"));
        var newest = _repositories.InternalCommit("three", ("src/app.txt", "v3\n"));
        _repositories.Publish(("src/app.txt", "v2\n"), ("README.md", "internal\n"));

        var (_, variables) = await RunAsync(new() { ["branch"] = "main", ["depth"] = "3" });

        Assert.Equal("true", variables["PUBLIC_VERIFIED"]);
        Assert.Equal(middle, variables["PUBLIC_MATCHED_COMMIT"]);
        var info = Lines(TaskLogLevel.Info).ToList();
        Assert.Equal($"{newest[..9]}: 1 difference(s)", info.Single(line => line.Contains("difference(s)", StringComparison.Ordinal)));
        Assert.True(info.IndexOf($"{newest[..9]}: 1 difference(s)") < info.FindIndex(line => line.StartsWith("Verified:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ACheckoutTooOldForTheDepth_IsRefused()
    {
        _repositories.InternalCommit("one", ("src/app.txt", "v1\n"), ("README.md", "internal\n"));
        _repositories.InternalCommit("two", ("src/app.txt", "v2\n"));
        _repositories.InternalCommit("three", ("src/app.txt", "v3\n"));
        _repositories.Publish(("src/app.txt", "v1\n"), ("README.md", "internal\n"));

        var (shallow, shallowVariables) = await RunAsync(new() { ["depth"] = "2" });
        _harness.Log.Clear();
        var (_, deepVariables) = await RunAsync(new() { ["depth"] = "3" });

        Assert.Equal(0, shallow.ExitCode);
        Assert.Equal("false", shallowVariables["PUBLIC_VERIFIED"]);
        Assert.Equal("true", deepVariables["PUBLIC_VERIFIED"]);
    }

    [Fact]
    public async Task TheWindowEndsAtThePublication_NotToday_SoLaterInternalCommitsDoNotHideTheMatch()
    {
        var exported = _repositories.InternalCommit("one", ("src/app.txt", "v1\n"), ("README.md", "internal\n"));
        _repositories.Publish(("src/app.txt", "v1\n"), ("README.md", "internal\n"));
        for (var n = 2; n <= 8; n++)
            _repositories.InternalCommit($"commit {n}", ("src/app.txt", $"v{n}\n"));

        var (_, variables) = await RunAsync(new() { ["depth"] = "5" });

        Assert.Equal("true", variables["PUBLIC_VERIFIED"]);
        Assert.Equal(exported, variables["PUBLIC_MATCHED_COMMIT"]);
    }

    [Fact]
    public async Task DocumentationEditedOnThePublicSide_IsTolerated_AnyOtherEditIsNot()
    {
        _repositories.InternalCommit("one", ("src/app.txt", "v1\n"), ("README.md", "internal\n"), ("docs/guide.md", "private\n"));
        _repositories.Publish(("src/app.txt", "v1\n"), ("README.md", "internal\n"));
        _repositories.PublicCommit("docs", ("README.md", "edited on the public side\n"), ("docs/public-only.md", "p\n"));

        var (_, tolerated) = await RunAsync(new() { ["branch"] = "main" });
        _repositories.PublicCommit("code", ("src/app.txt", "tampered\n"));
        _harness.Log.Clear();
        var (_, refused) = await RunAsync(new() { ["branch"] = "main" });

        Assert.Equal("true", tolerated["PUBLIC_VERIFIED"]);
        Assert.Equal("false", refused["PUBLIC_VERIFIED"]);
        Assert.Contains("  changed: src/app.txt", Lines(TaskLogLevel.Warning));
    }

    [Fact]
    public async Task BinaryFilesAndOtherEncodings_AreComparedByTheirBytes()
    {
        _repositories.InternalCommitBytes("one", ("src/data.bin", [0, 1, 2, 0xFF]), ("src/latin1.txt", [0x63, 0x61, 0x66, 0xE9, 0x0A]), ("README.md", "x\n"u8.ToArray()));
        _repositories.PublishBytes(("src/data.bin", [0, 1, 2, 0xFF]), ("src/latin1.txt", [0x63, 0x61, 0x66, 0xE9, 0x0A]), ("README.md", "x\n"u8.ToArray()));

        var (_, same) = await RunAsync(new() { ["branch"] = "HEAD" });
        _repositories.PublicCommitBytes("one byte", ("src/latin1.txt", [0x63, 0x61, 0x66, 0xC3, 0xA9, 0x0A]));
        _harness.Log.Clear();
        var (_, different) = await RunAsync(new() { ["branch"] = "HEAD" });

        Assert.Equal("true", same["PUBLIC_VERIFIED"]);
        Assert.Equal("false", different["PUBLIC_VERIFIED"]);
        Assert.Contains("  changed: src/latin1.txt", Lines(TaskLogLevel.Warning));
    }

    [Fact]
    public async Task ACandidateWhoseExportWouldBeRefused_IsSkipped_AndAnOlderOneCanStillMatch()
    {
        var older = _repositories.InternalCommit("one", ("src/app.txt", "v1\n"), ("README.md", "internal\n"));
        var unlisted = _repositories.InternalCommit("two", ("newdir/x.txt", "x\n"));
        _repositories.Publish(("src/app.txt", "v1\n"), ("README.md", "internal\n"));

        var (_, variables) = await RunAsync(new() { ["branch"] = "HEAD" });

        Assert.Equal("true", variables["PUBLIC_VERIFIED"]);
        Assert.Equal(older, variables["PUBLIC_MATCHED_COMMIT"]);
        Assert.Contains(
            $"{unlisted[..9]}: its export would be refused (top-level entries that are neither listed nor excluded in boundary.json: newdir)",
            Lines(TaskLogLevel.Info));
    }

    [Fact]
    public async Task ACommitWithoutBoundary_IsNotPublishable_AndTheCommitsBeforeItAreStillTried()
    {
        var withBoundary = _repositories.InternalCommit("one", ("src/app.txt", "v1\n"), ("README.md", "internal\n"));
        var without = _repositories.InternalCommitWithoutBoundary("two", ("src/app.txt", "v2\n"));
        _repositories.Publish(("src/app.txt", "v1\n"), ("README.md", "internal\n"));

        var (_, variables) = await RunAsync(new() { ["branch"] = "HEAD" });

        Assert.Equal("true", variables["PUBLIC_VERIFIED"]);
        Assert.Equal(withBoundary, variables["PUBLIC_MATCHED_COMMIT"]);
        Assert.Contains($"{without[..9]}: no boundary.json, not a publishable commit", Lines(TaskLogLevel.Info));
    }

    [Fact]
    public async Task ACommitWhoseBoundaryIsNotJson_IsReadAsHavingNone()
    {
        var good = _repositories.InternalCommit("one", ("src/app.txt", "v1\n"), ("README.md", "internal\n"));
        var broken = _repositories.InternalCommit("two", ("deploy/public-distribution/boundary.json", "{ not json"));
        _repositories.Publish(("src/app.txt", "v1\n"), ("README.md", "internal\n"));
        // The workspace keeps a usable boundary on disk, whatever the last commit holds.
        _repositories.WriteWorkspaceBoundary();

        var (_, variables) = await RunAsync(new() { ["branch"] = "HEAD" });

        Assert.Equal(good, variables["PUBLIC_MATCHED_COMMIT"]);
        Assert.Contains($"{broken[..9]}: no boundary.json, not a publishable commit", Lines(TaskLogLevel.Info));
    }

    [Fact]
    public async Task APathThatIsNotAGitCheckout_FailsTheStep()
    {
        _repositories.InternalCommit("one", ("src/app.txt", "v1\n"));
        var notRepository = Directory.CreateDirectory(Path.Combine(_harness.Root, "plain")).FullName;

        var (result, variables) = await RunAsync(new() { ["public_checkout"] = "../plain" });

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Equal($"{Prefix}'{notRepository}' is not a git checkout", result.FailureReason);
        Assert.Empty(variables);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("two")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    public async Task ADepthThatIsNotAPositiveInteger_FailsTheStep(string depth)
    {
        _repositories.InternalCommit("one", ("src/app.txt", "v1\n"));
        _repositories.Publish(("src/app.txt", "v1\n"));

        var (result, _) = await RunAsync(new() { ["depth"] = depth });

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Equal(Prefix + "depth must be a positive integer", result.FailureReason);
    }

    [Fact]
    public async Task ABranchThatLooksLikeAnOption_FailsTheStep()
    {
        _repositories.InternalCommit("one", ("src/app.txt", "v1\n"));
        _repositories.Publish(("src/app.txt", "v1\n"));

        var (result, _) = await RunAsync(new() { ["branch"] = "--all" });

        Assert.Equal(Prefix + "branch '--all' is not a branch name", result.FailureReason);
    }

    [Fact]
    public async Task ABranchWithNoCommitBeforeThePublication_FailsTheStep()
    {
        // The public repository is dated before the first internal commit.
        _repositories.Publish(("src/app.txt", "v1\n"));
        _repositories.InternalCommit("one", ("src/app.txt", "v1\n"));

        var (result, variables) = await RunAsync(new() { ["branch"] = "HEAD" });

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.StartsWith(Prefix + "branch 'HEAD' has no commit made before ", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(variables);
    }

    [Fact]
    public async Task ABranchGitDoesNotKnow_FailsTheStepWithGitsMessage()
    {
        _repositories.InternalCommit("one", ("src/app.txt", "v1\n"));
        _repositories.Publish(("src/app.txt", "v1\n"));

        var (result, _) = await RunAsync(new() { ["branch"] = "no-such-branch" });

        Assert.StartsWith(Prefix + "git rev-list --max-count=20 --before=", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("failed with exit code", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWorkspaceWithoutABoundary_FailsTheStep()
    {
        _repositories.InternalCommit("one", ("src/app.txt", "v1\n"));
        _repositories.Publish(("src/app.txt", "v1\n"));
        File.Delete(Path.Combine(_harness.Workspace, "deploy", "public-distribution", "boundary.json"));

        var (result, _) = await RunAsync(new());

        Assert.Equal(Prefix + "deploy/public-distribution/boundary.json not found in the workspace.", result.FailureReason);
    }

    [Fact]
    public async Task TheVerification_LeavesNoTemporaryFolderBehind()
    {
        _repositories.InternalCommit("one", ("src/app.txt", "v1\n"), ("README.md", "internal\n"));
        _repositories.Publish(("src/app.txt", "v1\n"), ("README.md", "internal\n"));
        var before = Directory.GetDirectories(Path.GetTempPath(), "aetheus-public-verify.*");

        await RunAsync(new() { ["branch"] = "HEAD" });

        Assert.Equal(before.Order(), Directory.GetDirectories(Path.GetTempPath(), "aetheus-public-verify.*").Order());
    }

    [Fact]
    public void TheFunction_IsRegisteredUnderTheNameOfItsCatalogEntry()
    {
        var descriptor = PipelineFunctionCatalog.VerifyPublicDistribution;

        Assert.Equal("verify-public-distribution", new VerifyPublicDistributionFunction().Name);
        Assert.Equal(PipelineFunctionAccess.Build, descriptor.Access);
        Assert.Equal(["public_checkout", "branch", "depth"], descriptor.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(["public_checkout"], descriptor.Parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name));
        Assert.Equal(["PUBLIC_VERIFIED", "PUBLIC_MATCHED_COMMIT"], descriptor.Outputs);
        Assert.Contains(PipelineFunctions.CreateAll(), function => function.Name == descriptor.Name);
    }

    private async Task<(ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync(Dictionary<string, string> parameters)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal) { ["public_checkout"] = "../public" };
        foreach (var (name, value) in parameters)
            all[name] = value;
        return await _harness.RunAsync(
            new VerifyPublicDistributionFunction(),
            new Dictionary<string, string> { [PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(all) },
            timeoutSeconds: 300);
    }

    private IEnumerable<string> Lines(TaskLogLevel level) => _harness.Log.Where(entry => entry.Level == level).Select(entry => entry.Line);

    private sealed class BoundaryFile
    {
        public Dictionary<string, string> Rename { get; init; } = [];

        public string[] Forbidden { get; init; } = [];

        public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["topLevel"] = new[] { "deploy", "src", "README.md", "launch-windows.ps1" },
            ["exclude"] = new[] { "docs/**", "deploy/public-distribution/boundary.json" },
            ["rename"] = Rename,
            ["dropTests"] = new Dictionary<string, string[]>(),
            ["forbidden"] = Forbidden,
            ["compareIgnore"] = new[] { "**/*.md", "docs/**" }
        });
    }

    /// <summary>The internal repository (the workspace) and the public checkout of one test, with a clock
    /// that moves one minute at each commit so that "before the publication" is exact.</summary>
    private sealed class Repositories
    {
        private readonly FunctionTestHarness _harness;
        private DateTimeOffset _clock = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        public Repositories(FunctionTestHarness harness)
        {
            _harness = harness;
            Internal = harness.Workspace;
            Public = Path.Combine(harness.Root, "public");
            Init(Internal);
            Init(Public);
        }

        public BoundaryFile Boundary { get; set; } = new();

        public string PublishedAt { get; private set; } = string.Empty;

        private string Internal { get; }

        private string Public { get; }

        public string InternalCommit(string message, params (string Path, string Text)[] files) =>
            InternalCommitBytes(message, [.. files.Select(file => (file.Path, Encoding.UTF8.GetBytes(file.Text)))]);

        public string InternalCommitBytes(string message, params (string Path, byte[] Bytes)[] files)
        {
            WriteWorkspaceBoundary();
            return Commit(Internal, message, files);
        }

        /// <summary>A commit that removes the boundary from the index while the workspace keeps its copy.</summary>
        public string InternalCommitWithoutBoundary(string message, params (string Path, string Text)[] files)
        {
            Write(Internal, files.Select(file => (file.Path, Encoding.UTF8.GetBytes(file.Text))));
            Git(Internal, "add", "--all");
            Git(Internal, "rm", "--quiet", "--cached", "deploy/public-distribution/boundary.json");
            return CommitStaged(Internal, message);
        }

        public void WriteWorkspaceBoundary() =>
            Write(Internal, [("deploy/public-distribution/boundary.json", Encoding.UTF8.GetBytes(Boundary.ToJson()))]);

        /// <summary>The first commit of the public repository, dated after every internal commit so far.</summary>
        public string Publish(params (string Path, string Text)[] files) =>
            PublishBytes([.. files.Select(file => (file.Path, Encoding.UTF8.GetBytes(file.Text)))]);

        public string PublishBytes(params (string Path, byte[] Bytes)[] files)
        {
            var sha = Commit(Public, "export", files);
            PublishedAt = Git(Public, "log", "-1", "--format=%cI").Trim();
            return sha;
        }

        public string PublicCommit(string message, params (string Path, string Text)[] files) =>
            PublicCommitBytes(message, [.. files.Select(file => (file.Path, Encoding.UTF8.GetBytes(file.Text)))]);

        public string PublicCommitBytes(string message, params (string Path, byte[] Bytes)[] files) => Commit(Public, message, files);

        private string Commit(string directory, string message, IEnumerable<(string Path, byte[] Bytes)> files)
        {
            Write(directory, files);
            Git(directory, "add", "--all");
            return CommitStaged(directory, message);
        }

        private string CommitStaged(string directory, string message)
        {
            _clock = _clock.AddMinutes(1);
            Git(directory, "commit", "--quiet", "--allow-empty", "-m", message);
            return Git(directory, "rev-parse", "HEAD").Trim();
        }

        private static void Write(string directory, IEnumerable<(string Path, byte[] Bytes)> files)
        {
            foreach (var (path, bytes) in files)
            {
                var full = Path.Combine(directory, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, bytes);
            }
        }

        private static void Init(string directory)
        {
            Directory.CreateDirectory(directory);
            RunGit(directory, null, "init", "--quiet", "--initial-branch", "main");
        }

        private string Git(string directory, params string[] arguments) => RunGit(directory, _clock, arguments);

        private static string RunGit(string directory, DateTimeOffset? at, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
                     {
                         "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "core.autocrlf=false",
                         "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + Path.GetTempPath()
                     }.Concat(arguments))
                startInfo.ArgumentList.Add(argument);
            if (at is { } date)
            {
                var stamp = date.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                startInfo.Environment["GIT_AUTHOR_DATE"] = stamp;
                startInfo.Environment["GIT_COMMITTER_DATE"] = stamp;
            }
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr}");
            return stdout.Result;
        }
    }
}
