// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

public sealed class GitleaksHistoryModeResolverTests
{
    [Fact]
    public async Task ReportAnnotator_PersistsAuditableRangeSelection()
    {
        var report = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(report, "{\"version\":\"2.1.0\",\"runs\":[{\"results\":[]}]}",
                TestContext.Current.CancellationToken);
            var annotated = await GitleaksHistoryReportAnnotator.AnnotateAsync(
                new ScannerManifestEntry { Key = "gitleaks-history" },
                report,
                new Dictionary<string, string>
                {
                    ["AETHEUS_GITLEAKS_HISTORY_MODE_RESOLVED"] = "release-range",
                    ["AETHEUS_GITLEAKS_BASE_SHA_RESOLVED"] = new string('a', 40),
                    ["AETHEUS_GITLEAKS_HEAD_SHA_RESOLVED"] = new string('b', 40),
                    ["AETHEUS_GITLEAKS_HISTORY_REASON_RESOLVED"] = "validated-release-range"
                },
                TestContext.Current.CancellationToken);

            Assert.True(annotated);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                report, TestContext.Current.CancellationToken));
            var metadata = document.RootElement.GetProperty("runs")[0].GetProperty("properties")
                .GetProperty("aetheusHistory");
            Assert.Equal("release-range", metadata.GetProperty("mode").GetString());
            Assert.Equal(new string('a', 40), metadata.GetProperty("baseSha").GetString());
            Assert.Equal(new string('b', 40), metadata.GetProperty("headSha").GetString());
        }
        finally
        {
            File.Delete(report);
        }
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("abc", false)]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", false)]
    public void IsCommit_AcceptsOnlyFullHexObjectIds(string value, bool expected)
    {
        Assert.Equal(expected, GitleaksHistoryModeResolver.IsCommit(value));
    }

    [Fact]
    public async Task ResolveAsync_ExplicitFull_DoesNotAcceptFreeFormGitOptions()
    {
        var head = new string('a', 40);
        var selection = await GitleaksHistoryModeResolver.ResolveAsync(
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>
            {
                ["AETHEUS_GITLEAKS_MODE"] = "full",
                ["BUILD_SOURCEVERSION"] = head,
                ["AETHEUS_GITLEAKS_LOG_OPTIONS"] = "--all --not-safe"
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("full", selection.Mode);
        Assert.Null(selection.LogOptions);
    }

    [Fact]
    public async Task ResolveAsync_InvalidMode_IsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => GitleaksHistoryModeResolver.ResolveAsync(
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>
            {
                ["AETHEUS_GITLEAKS_MODE"] = "--all",
                ["BUILD_SOURCEVERSION"] = new string('a', 40)
            },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_LinearRangeIncludesAddedThenDeletedSecretCommits()
    {
        var repository = CreateRepository();
        try
        {
            Write(repository, "safe.txt", "safe");
            Commit(repository, "baseline");
            var baseline = Git(repository, "rev-parse", "HEAD");
            Write(repository, "secret.txt", "token-that-will-be-deleted");
            Commit(repository, "add secret");
            File.Delete(Path.Combine(repository, "secret.txt"));
            Git(repository, "add", "-A");
            Git(repository, "commit", "-m", "delete secret");
            var head = Git(repository, "rev-parse", "HEAD");

            var selection = await GitleaksHistoryModeResolver.ResolveAsync(
                repository,
                Environment("release-range", baseline, head),
                TestContext.Current.CancellationToken);

            Assert.Equal("release-range", selection.Mode);
            Assert.Equal($"{baseline}..{head}", selection.LogOptions);
            var count = int.Parse(
                Git(repository, "rev-list", "--count", selection.LogOptions!),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(2, count);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task ResolveAsync_ScannerContractChangeForcesFullMode()
    {
        var repository = CreateRepository();
        try
        {
            Write(repository, "safe.txt", "safe");
            Commit(repository, "baseline");
            var baseline = Git(repository, "rev-parse", "HEAD");
            Write(repository, "scanner-manifest.json", "{}");
            Commit(repository, "rules update");
            var head = Git(repository, "rev-parse", "HEAD");

            var selection = await GitleaksHistoryModeResolver.ResolveAsync(
                repository,
                Environment("release-range", baseline, head),
                TestContext.Current.CancellationToken);

            Assert.Equal("full", selection.Mode);
            Assert.Equal("scanner-contract-changed", selection.Reason);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task ResolveAsync_SecurityHistoryDefinitionChangeForcesFullMode()
    {
        var repository = CreateRepository();
        try
        {
            Write(repository, "safe.txt", "safe");
            Commit(repository, "baseline");
            var baseline = Git(repository, "rev-parse", "HEAD");
            Write(repository, ".pipeline/aetheus-security-history.yaml", "name: aetheus-security-history");
            Commit(repository, "history scan definition update");
            var head = Git(repository, "rev-parse", "HEAD");

            var selection = await GitleaksHistoryModeResolver.ResolveAsync(
                repository,
                Environment("release-range", baseline, head),
                TestContext.Current.CancellationToken);

            Assert.Equal("full", selection.Mode);
            Assert.Equal("scanner-contract-changed", selection.Reason);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task ResolveAsync_MergeRangeRemainsBoundedWhenBaselineIsAncestor()
    {
        var repository = CreateRepository();
        try
        {
            Write(repository, "root.txt", "root");
            Commit(repository, "baseline");
            var baseline = Git(repository, "rev-parse", "HEAD");
            var mainBranch = Git(repository, "branch", "--show-current");
            Git(repository, "checkout", "-b", "feature");
            Write(repository, "feature.txt", "feature");
            Commit(repository, "feature");
            Git(repository, "checkout", mainBranch);
            Write(repository, "main.txt", "main");
            Commit(repository, "main");
            Git(repository, "merge", "--no-ff", "feature", "-m", "merge");
            var head = Git(repository, "rev-parse", "HEAD");

            var selection = await GitleaksHistoryModeResolver.ResolveAsync(
                repository, Environment("release-range", baseline, head),
                TestContext.Current.CancellationToken);

            Assert.Equal("release-range", selection.Mode);
            Assert.Equal($"{baseline}..{head}", selection.LogOptions);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task ResolveAsync_NonAncestorBaselineForcesFullMode()
    {
        var repository = CreateRepository();
        try
        {
            Write(repository, "root.txt", "root");
            Commit(repository, "root");
            var mainBranch = Git(repository, "branch", "--show-current");
            Git(repository, "checkout", "-b", "side");
            Write(repository, "side.txt", "side");
            Commit(repository, "side");
            var baseline = Git(repository, "rev-parse", "HEAD");
            Git(repository, "checkout", mainBranch);
            Write(repository, "main.txt", "main");
            Commit(repository, "main");
            var head = Git(repository, "rev-parse", "HEAD");

            var selection = await GitleaksHistoryModeResolver.ResolveAsync(
                repository, Environment("release-range", baseline, head),
                TestContext.Current.CancellationToken);

            Assert.Equal("full", selection.Mode);
            Assert.Equal("baseline-not-ancestor", selection.Reason);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task ResolveAsync_ShallowCheckoutIsDeepenedWithinBound()
    {
        var repository = CreateRepository();
        var shallow = Directory.CreateTempSubdirectory("aetheus-gitleaks-shallow-").FullName;
        try
        {
            Write(repository, "one.txt", "one");
            Commit(repository, "one");
            var baseline = Git(repository, "rev-parse", "HEAD");
            Write(repository, "two.txt", "two");
            Commit(repository, "two");
            Write(repository, "three.txt", "three");
            Commit(repository, "three");
            Directory.Delete(shallow);
            Git(Path.GetDirectoryName(shallow)!, "clone", "--depth", "1", new Uri(repository).AbsoluteUri, shallow);
            var head = Git(shallow, "rev-parse", "HEAD");

            var selection = await GitleaksHistoryModeResolver.ResolveAsync(
                shallow, Environment("release-range", baseline, head),
                TestContext.Current.CancellationToken);

            Assert.Equal("release-range", selection.Mode);
            Assert.Equal("validated-release-range", selection.Reason);
        }
        finally
        {
            if (Directory.Exists(shallow)) DeleteRepository(shallow);
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task ResolveAsync_MissingBaselineForcesFullMode()
    {
        var selection = await GitleaksHistoryModeResolver.ResolveAsync(
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>
            {
                ["AETHEUS_GITLEAKS_MODE"] = "release-range",
                ["BUILD_SOURCEVERSION"] = new string('a', 40)
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("full", selection.Mode);
        Assert.Equal("missing-baseline", selection.Reason);
    }

    [Fact]
    public async Task ResolveAsync_ShortHead_IsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => GitleaksHistoryModeResolver.ResolveAsync(
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string> { ["AETHEUS_GITLEAKS_MODE"] = "full", ["BUILD_SOURCEVERSION"] = "abc123" },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_CheckoutOnAnotherCommitForcesFullMode()
    {
        var repository = CreateRepository();
        try
        {
            Write(repository, "one.txt", "one");
            Commit(repository, "one");
            var baseline = Git(repository, "rev-parse", "HEAD");

            var selection = await GitleaksHistoryModeResolver.ResolveAsync(
                repository, Environment("release-range", baseline, new string('c', 40)),
                TestContext.Current.CancellationToken);

            Assert.Equal("full", selection.Mode);
            Assert.Equal("head-mismatch", selection.Reason);
            Assert.Equal(baseline, selection.BaselineSha);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task ResolveEnvironmentAsync_OtherScanner_LeavesTheEnvironmentAsIs()
    {
        var env = new Dictionary<string, string> { ["AETHEUS_GITLEAKS_MODE"] = "--all" };

        var resolved = await GitleaksHistoryModeResolver.ResolveEnvironmentAsync(
            new ScannerManifestEntry { Key = "trivy" }, Directory.GetCurrentDirectory(), env,
            (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Same(env, resolved);
    }

    [Fact]
    public async Task ResolveEnvironmentAsync_HistoryScanner_PublishesTheSelectionAndDropsFreeFormOptions()
    {
        var head = new string('A', 40);
        var output = new List<string>();

        var resolved = await GitleaksHistoryModeResolver.ResolveEnvironmentAsync(
            new ScannerManifestEntry { Key = "gitleaks-history" },
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>
            {
                ["BUILD_SOURCEVERSION"] = head,
                ["AETHEUS_GITLEAKS_LOG_OPTIONS"] = "--all --not-safe"
            },
            (line, _) => { output.Add(line); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.False(resolved.ContainsKey("AETHEUS_GITLEAKS_LOG_OPTIONS"));
        Assert.Equal("full", resolved["AETHEUS_GITLEAKS_HISTORY_MODE_RESOLVED"]);
        Assert.Equal(string.Empty, resolved["AETHEUS_GITLEAKS_BASE_SHA_RESOLVED"]);
        Assert.Equal(new string('a', 40), resolved["AETHEUS_GITLEAKS_HEAD_SHA_RESOLVED"]);
        Assert.Equal("explicit-full", resolved["AETHEUS_GITLEAKS_HISTORY_REASON_RESOLVED"]);
        Assert.Equal(
        [
            $"Gitleaks history mode=full, baseSha=none, headSha={new string('a', 40)}, reason=explicit-full.",
            "##aetheus[setvariable name=GITLEAKS_HISTORY_MODE]full",
            "##aetheus[setvariable name=GITLEAKS_BASE_SHA]none",
            $"##aetheus[setvariable name=GITLEAKS_HEAD_SHA]{new string('a', 40)}",
        ], output);
    }


    private static Dictionary<string, string> Environment(string mode, string baseline, string head) => new()
    {
        ["AETHEUS_GITLEAKS_MODE"] = mode,
        ["DELIVERY_BASELINE_SOURCE_SHA"] = baseline,
        ["BUILD_SOURCEVERSION"] = head,
        ["AETHEUS_GIT_HISTORY_DEPTH"] = "10"
    };

    private static string CreateRepository()
    {
        var path = Directory.CreateTempSubdirectory("aetheus-gitleaks-range-").FullName;
        Git(path, "init");
        Git(path, "config", "user.email", "tests@aetheus.invalid");
        Git(path, "config", "user.name", "Aetheus Tests");
        return path;
    }

    private static void Write(string repository, string relativePath, string content)
    {
        var path = Path.Combine(repository, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Commit(string repository, string message)
    {
        Git(repository, "add", "-A");
        Git(repository, "commit", "-m", message);
    }

    private static string Git(string repository, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        // WHY: pre-push hook env inheritance incidents - any repository-local git variable (GIT_DIR,
        // GIT_COMMON_DIR, ...) would redirect this fixture git call to the real repository.
        GitRepositoryEnvironment.Neutralize(startInfo.Environment);
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    private static void DeleteRepository(string repository)
    {
        foreach (var file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        foreach (var directory in Directory.EnumerateDirectories(repository, "*", SearchOption.AllDirectories))
            File.SetAttributes(directory, FileAttributes.Normal);
        Directory.Delete(repository, recursive: true);
    }
}
