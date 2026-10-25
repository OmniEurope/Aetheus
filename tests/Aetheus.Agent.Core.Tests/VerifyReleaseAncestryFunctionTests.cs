// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the former <c>verify-release-ancestry.sh</c> script, run through the real executor with
/// real Git on throwaway repositories built like the ones the script ran on
/// (<c>TestData/release-ancestry-cases.json</c>, <see cref="ScriptReferenceData"/>; written by a driver
/// that ran the script with <c>sh</c> in the repository, commit ids recorded as placeholders: <c>{key}</c>,
/// <c>{key:upper}</c>, <c>{key:short}</c> as <c>git rev-parse --short</c> printed it, <c>{key:N}</c> and
/// <c>{key:A:B}</c> for the truncations the provenance used). Every line the script printed must be
/// logged verbatim, stdout at Info, stderr otherwise, and a refusal must fail with its first line.
/// </summary>
public sealed partial class VerifyReleaseAncestryFunctionTests : IDisposable
{
    private const string File = "release-ancestry-cases.json";
    private const string Prefix = "Function 'verify-release-ancestry' failed: ";
    private const string Provenance = ".pipeline-artifacts/source-commit";
    private readonly FunctionTestHarness _harness = new("aetheus-release-ancestry");

    public void Dispose()
    {
        // Git writes its objects read-only, which Windows refuses to delete.
        foreach (var path in Directory.EnumerateFiles(_harness.Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            System.IO.File.SetAttributes(path, FileAttributes.Normal);
        _harness.Dispose();
    }

    public static TheoryData<string> Cases() => ScriptReferenceData.CaseNames(File);

    [GeneratedRegex(@"\{(\w+)(?::(\w+))?(?::(\d+))?\}")]
    private static partial Regex Placeholder();

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryCase_LogsAndDecidesLikeTheScript(string name)
    {
        var testCase = ScriptReferenceData.Case(File, name);
        var repository = new ThrowawayRepository(_harness.Root);
        var ids = repository.Build(testCase.GetProperty("shape").GetString()!);
        var workspace = repository.Work;
        WriteProvenance(workspace, testCase, ids);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["source_commit_file"] = Provenance };
        if (testCase.TryGetProperty("deployRef", out var deployRef))
            parameters["deploy_ref"] = deployRef.GetString()!;

        var result = await RunAsync(workspace, parameters);

        var expand = (string text) => Expand(text, ids, repository);
        var stdout = Lines(expand(testCase.GetProperty("stdout").GetString()!));
        var stderr = Lines(expand(testCase.GetProperty("stderr").GetString()!));
        var logged = _harness.Log.Skip(1).SkipLast(1).ToList();
        Assert.Equal(stdout, logged.Where(entry => entry.Level == TaskLogLevel.Info).Select(entry => entry.Line));
        Assert.Equal(stderr, logged.Where(entry => entry.Level != TaskLogLevel.Info).Select(entry => entry.Line));
        var exitCode = testCase.GetProperty("exitCode").GetInt32();
        if (exitCode == 0)
        {
            Assert.Equal(0, result.ExitCode);
            return;
        }
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Equal(
            exitCode == 1 ? Prefix + stderr[0] : $"{Prefix}The deploy ref '{parameters["deploy_ref"]}' does not resolve: git rev-parse --short exited with code {exitCode}.",
            result.FailureReason);
    }

    [Fact]
    public void TheReferenceData_CoversTheScriptTest()
    {
        var names = ScriptReferenceData.CaseNames(File).Select(row => row.Data).ToHashSet();

        // verify-release-ancestry.test.mjs, then the shallow and fetch paths it did not cover.
        Assert.Subset(names, new HashSet<string>
        {
            "contains", "never-received", "abbreviated", "missing-file", "empty-file", "not-hex", "unknown-commit",
            "shallow-deepened", "shallow-refused", "shallow-unprovable", "shallow-unknown", "fetched-by-id"
        });
    }

    [Fact]
    public async Task EveryGitCommand_RunsInTheWorkspaceWithoutACredentialPrompt_InTheScriptOrder()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        _harness.Write("ws/" + Provenance, sha + "\n");
        // The commit is missing until the fetch by id brings it.
        var fetched = false;
        var tools = new FakeFunctionTools
        {
            OnRun = (process, _) => Task.FromResult<(int, string[])>(string.Join(' ', process.ArgumentList) switch
            {
                "rev-parse --is-shallow-repository" => (0, ["true"]),
                "rev-parse --short HEAD" => (0, ["0123456"]),
                "merge-base --is-ancestor " + sha + " HEAD" => (0, []),
                "rev-parse --short " + sha => (0, ["0123456"]),
                "fetch --quiet origin " + sha => Fetch(),
                "cat-file -e " + sha + "^{commit}" => (fetched ? 0 : 1, []),
                _ => (1, [])
            })
        };
        (int, string[]) Fetch()
        {
            fetched = true;
            return (0, []);
        }

        var (result, _) = await _harness.RunAsync(new VerifyReleaseAncestryFunction(tools), Environment(new() { ["source_commit_file"] = Provenance }));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                "rev-parse --is-shallow-repository", "fetch --quiet --unshallow origin", "cat-file -e " + sha + "^{commit}",
                "fetch --quiet origin " + sha, "rev-parse --short HEAD", "cat-file -e " + sha + "^{commit}",
                "merge-base --is-ancestor " + sha + " HEAD", "rev-parse --short " + sha
            ],
            tools.Runs.Select(run => string.Join(' ', run.ArgumentList)));
        Assert.All(tools.Runs, run =>
        {
            Assert.Equal(_harness.Workspace, run.WorkingDirectory);
            Assert.Equal("0", run.Environment["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("/bin/false", run.Environment["GIT_ASKPASS"]);
        });
    }

    private async Task<ExecutorResult> RunAsync(string workspace, Dictionary<string, string> parameters)
    {
        var env = Environment(parameters);
        env[PipelineFunctionRequest.WorkspaceVariable] = workspace;
        var executor = new PipelineFunctionOperationExecutor([new VerifyReleaseAncestryFunction()]);
        return await executor.ExecuteAsync(
            OperationKind.PipelineFunction, PipelineFunctionCatalog.VerifyReleaseAncestry.Name, env, 120,
            (line, level) =>
            {
                lock (_harness.Log)
                    _harness.Log.Add((line, level));
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string> Environment(Dictionary<string, string> parameters) =>
        new(StringComparer.Ordinal) { [PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(parameters) };

    private static void WriteProvenance(string workspace, JsonElement testCase, Dictionary<string, string> ids)
    {
        var path = Path.Combine(workspace, Provenance);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!testCase.TryGetProperty("provenance", out var provenance))
            return;
        if (provenance.GetString() == "<directory>")
            Directory.CreateDirectory(path);
        else
            System.IO.File.WriteAllText(path, Expand(provenance.GetString()!, ids, null));
    }

    private static string Expand(string text, Dictionary<string, string> ids, ThrowawayRepository? repository) =>
        Placeholder().Replace(text, match =>
        {
            var id = ids[match.Groups[1].Value];
            var first = match.Groups[2].Value;
            return first switch
            {
                "" => id,
                "upper" => id.ToUpperInvariant(),
                "short" => repository!.Git("rev-parse", "--short", id),
                _ when match.Groups[3].Success => id[int.Parse(first)..int.Parse(match.Groups[3].Value)],
                _ => id[..int.Parse(first)]
            };
        });

    private static List<string> Lines(string text) =>
        text.Length == 0 ? [] : [.. text.TrimEnd('\n').Split('\n')];

    /// <summary>The repositories of the reference driver: <c>plain</c> (the script test's: one commit on
    /// main, one on an unmerged branch), <c>shallow-reachable</c> and <c>shallow-unreachable</c> (a depth-1
    /// clone of an origin with main c1-c2-c3 and side s1 from c1, its origin then reachable or gone) and
    /// <c>late</c> (a complete clone, then l1 committed on a new origin branch).</summary>
    private sealed class ThrowawayRepository(string root)
    {
        public string Work { get; } = Path.Combine(root, "repo");

        private string Origin { get; } = Path.Combine(root, "origin");

        public Dictionary<string, string> Build(string shape) => shape switch
        {
            "plain" => Plain(),
            "shallow-reachable" => Shallow(reachable: true),
            "shallow-unreachable" => Shallow(reachable: false),
            "late" => Late(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown repository shape.")
        };

        public string Git(params string[] arguments) => Run(Work, arguments);

        private Dictionary<string, string> Plain()
        {
            Init(Work);
            var onBranch = Commit(Work, "a.txt", "a", "on the deploy branch");
            Run(Work, "checkout", "--quiet", "-b", "unmerged");
            var offBranch = Commit(Work, "b.txt", "b", "never promoted");
            Run(Work, "checkout", "--quiet", "main");
            return new() { ["onBranch"] = onBranch, ["offBranch"] = offBranch };
        }

        private Dictionary<string, string> Shallow(bool reachable)
        {
            Init(Origin);
            var c1 = Commit(Origin, "a.txt", "1", "c1");
            Run(Origin, "checkout", "--quiet", "-b", "side");
            var s1 = Commit(Origin, "s.txt", "s", "s1");
            Run(Origin, "checkout", "--quiet", "main");
            var c2 = Commit(Origin, "a.txt", "2", "c2");
            var c3 = Commit(Origin, "a.txt", "3", "c3");
            Run(root, "clone", "--quiet", "--depth", "1", "--no-single-branch", Url(Origin), "repo");
            if (!reachable)
                Run(Work, "remote", "set-url", "origin", Url(Path.Combine(root, "gone")));
            return new() { ["c1"] = c1, ["c2"] = c2, ["c3"] = c3, ["s1"] = s1 };
        }

        private Dictionary<string, string> Late()
        {
            Init(Origin);
            var c1 = Commit(Origin, "a.txt", "1", "c1");
            Run(root, "clone", "--quiet", Url(Origin), "repo");
            Run(Origin, "checkout", "--quiet", "-b", "late");
            var l1 = Commit(Origin, "l.txt", "l", "l1");
            Run(Origin, "checkout", "--quiet", "main");
            return new() { ["c1"] = c1, ["l1"] = l1 };
        }

        private static string Url(string path) => "file://" + path.Replace('\\', '/');

        private static void Init(string directory)
        {
            Directory.CreateDirectory(directory);
            Run(directory, "init", "--quiet", "--initial-branch", "main");
            Run(directory, "config", "user.email", "test@example.invalid");
            Run(directory, "config", "user.name", "Ancestry Gate Test");
        }

        private static string Commit(string directory, string file, string content, string message)
        {
            System.IO.File.WriteAllText(Path.Combine(directory, file), content);
            Run(directory, "add", ".");
            Run(directory, "commit", "--quiet", "-m", message);
            return Run(directory, "rev-parse", "HEAD");
        }

        private static string Run(string directory, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("core.autocrlf=false");
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr}");
            return stdout.Result.Trim();
        }
    }
}
