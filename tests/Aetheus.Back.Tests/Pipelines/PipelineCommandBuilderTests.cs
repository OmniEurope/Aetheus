// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Tests.Shared;

namespace Aetheus.Back.Tests.Pipelines;

public class PipelineCommandBuilderTests
{
    private const string Guard = "; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE };";

    [Fact]
    public void BuildCloneCommands_CheckOutImmutableSourceVersionWhenProvided()
    {
        var linux = PipelineCommandBuilder.BuildLinuxCloneCommand("/tmp/workspace");
        var windows = PipelineCommandBuilder.BuildWindowsCloneCommand(@"C:\workspace");

        Assert.Contains("git cat-file -e \"${BUILD_SOURCEVERSION}^{commit}\"", linux, StringComparison.Ordinal);
        Assert.Contains("GIT_HISTORY_DEPTH=\"${AETHEUS_GIT_HISTORY_DEPTH:-1}\"", linux, StringComparison.Ordinal);
        Assert.Contains("git fetch --depth \"$GIT_HISTORY_DEPTH\" origin \"${DEFAULT_BRANCH:-main}\"", linux, StringComparison.Ordinal);
        Assert.Contains("git fetch --depth 1 origin \"${BUILD_SOURCEVERSION}\" || true", linux, StringComparison.Ordinal);
        Assert.Contains("git fetch --depth 1 origin '+refs/heads/*:refs/remotes/origin/*'", linux, StringComparison.Ordinal);
        Assert.Contains("git fetch --deepen 1000 origin '+refs/heads/*:refs/remotes/origin/*'", linux, StringComparison.Ordinal);
        Assert.DoesNotContain("--unshallow", linux, StringComparison.Ordinal);
        Assert.Contains("git checkout --detach \"${BUILD_SOURCEVERSION}\"", linux, StringComparison.Ordinal);
        Assert.Contains("git cat-file -e \"$env:BUILD_SOURCEVERSION`^{commit}\"", windows, StringComparison.Ordinal);
        Assert.Contains("$env:AETHEUS_GIT_HISTORY_DEPTH", windows, StringComparison.Ordinal);
        Assert.Contains("git fetch --depth $historyDepth origin $branch", windows, StringComparison.Ordinal);
        Assert.Contains("git fetch --depth 1 origin $env:BUILD_SOURCEVERSION", windows, StringComparison.Ordinal);
        Assert.Contains("git fetch --depth 1 origin '+refs/heads/*:refs/remotes/origin/*'", windows, StringComparison.Ordinal);
        Assert.Contains("git fetch --deepen 1000 origin '+refs/heads/*:refs/remotes/origin/*'", windows, StringComparison.Ordinal);
        Assert.DoesNotContain("--unshallow", windows, StringComparison.Ordinal);
        Assert.Contains("git checkout --detach $env:BUILD_SOURCEVERSION", windows, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCloneCommands_RouteMandatoryGitNetworkCallsThroughThreeAttemptRetry()
    {
        var linux = PipelineCommandBuilder.BuildLinuxCloneCommand("/tmp/workspace");
        var windows = PipelineCommandBuilder.BuildWindowsCloneCommand(@"C:\workspace");

        Assert.Contains("local attempt=1 max=3", linux, StringComparison.Ordinal);
        Assert.Contains("git_with_retry git fetch --depth \"$GIT_HISTORY_DEPTH\" origin", linux, StringComparison.Ordinal);
        Assert.Contains("git_with_retry git fetch --depth 1 origin \"${DEFAULT_BRANCH:-main}\"", linux, StringComparison.Ordinal);
        Assert.Contains("git_with_retry git clone --branch", linux, StringComparison.Ordinal);
        Assert.Contains("return \"$rc\"", linux, StringComparison.Ordinal);

        Assert.Contains("$max = 3", windows, StringComparison.Ordinal);
        Assert.Contains("Invoke-GitWithRetry 'fetch' { git fetch --depth $historyDepth origin $branch }", windows, StringComparison.Ordinal);
        Assert.Contains("Invoke-GitWithRetry 'fetch' { git fetch --depth 1 origin $branch }", windows, StringComparison.Ordinal);
        Assert.Contains("Invoke-GitWithRetry 'clone' { git clone --branch", windows, StringComparison.Ordinal);
        Assert.Contains("throw \"git $Name failed with exit code $code.\"", windows, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxGitWithRetry_SucceedsWhenGitFailsTwiceThenSucceeds()
    {
        var result = await RunRetryFunctionAsync(failuresBeforeSuccess: 2);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("(attempt 1 of 3, exit 128); retrying in 10 s.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("(attempt 2 of 3, exit 128); retrying in 30 s.", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("git calls=3", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("sleeps=10 30", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxGitWithRetry_FailsWithGitExitCodeAfterThreeAttempts()
    {
        var result = await RunRetryFunctionAsync(failuresBeforeSuccess: 3);

        Assert.True(result.ExitCode == 128, result.Stderr);
        Assert.Contains("git calls=3", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("giving up", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("reached", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxGitWithRetry_DoesNotRetryAnAuthenticationFailure()
    {
        var result = await RunRetryFunctionAsync(failuresBeforeSuccess: 3,
            gitError: "fatal: Authentication failed for 'https://api.example.test/git/1/aetheus.git/'");

        Assert.True(result.ExitCode == 128, result.Stderr);
        Assert.Contains("git calls=1", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("fatal: Authentication failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("not retrying", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("sleeps=\n", result.Stdout.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxGitWithRetry_DoesNotRetryAMissingRepository()
    {
        var result = await RunRetryFunctionAsync(failuresBeforeSuccess: 3,
            gitError: "remote: Repository not found.\nfatal: repository 'https://api.example.test/git/1/missing.git/' not found");

        Assert.True(result.ExitCode == 128, result.Stderr);
        Assert.Contains("git calls=1", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("not retrying", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxGitWithRetry_KeepsTheTransientGitErrorInTheLogAndRetries()
    {
        var result = await RunRetryFunctionAsync(failuresBeforeSuccess: 1,
            gitError: "fatal: unable to access 'https://api.example.test/git/1/aetheus.git/': Failed to connect to api.example.test port 443: Connection refused");

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("Connection refused", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("git calls=2", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCloneCommands_ClearAFailedCloneOnlyWhenTheWorkspaceStartedEmpty()
    {
        var linux = PipelineCommandBuilder.BuildLinuxCloneCommand("/tmp/workspace");
        var windows = PipelineCommandBuilder.BuildWindowsCloneCommand(@"C:\workspace");

        Assert.Contains("[ -n \"$(ls -A)\" ] || clone_cleanup=clear_partial_clone", linux, StringComparison.Ordinal);
        Assert.Contains("GIT_RETRY_CLEANUP=\"$clone_cleanup\" git_with_retry git clone", linux, StringComparison.Ordinal);
        Assert.Contains("$cloneCleanup = if (Get-ChildItem -Force | Select-Object -First 1) { $null } else { $clearPartialClone }", windows, StringComparison.Ordinal);
        Assert.Contains("$env:REPOSITORY_URL . } $cloneCleanup", windows, StringComparison.Ordinal);
        Assert.Contains("not retrying", windows, StringComparison.Ordinal);
    }

    // On Windows the PATH bash can be the WSL launcher, which cannot run a Windows temp path: use Git for Windows bash.
    private static string BashExecutable()
    {
        if (!OperatingSystem.IsWindows())
            return "bash";
        var gitBash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "bash.exe");
        return File.Exists(gitBash) ? gitBash : "bash";
    }

    // Runs the generated git_with_retry under set -e with a fake git (function) that fails the requested
    // number of times with exit 128, and a fake sleep that records the delays instead of waiting.
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunRetryFunctionAsync(int failuresBeforeSuccess, string gitError = "")
    {
        var script = PipelineCommandBuilder.BuildLinuxCloneCommand("/tmp/workspace");
        var start = script.IndexOf("git_with_retry() {", StringComparison.Ordinal);
        var end = script.IndexOf("\n}\n", start, StringComparison.Ordinal) + 3;
        var function = script[start..end];
        var harness =
            "set -e\ncalls=0\nsleeps=''\n" +
            "GIT_ERROR='" + gitError.Replace("'", "'\\''", StringComparison.Ordinal) + "'\n" +
            "git() { calls=$((calls + 1)); if [ \"$calls\" -le " + failuresBeforeSuccess + " ]; then [ -z \"$GIT_ERROR\" ] || printf '%s\\n' \"$GIT_ERROR\" >&2; return 128; fi; return 0; }\n" +
            "sleep() { sleeps=\"${sleeps:+$sleeps }$1\"; }\n" +
            function +
            "trap 'echo \"git calls=$calls\"; echo \"sleeps=$sleeps\"' EXIT\n" +
            "git_with_retry git fetch origin main\necho reached\n";

        var path = Path.Combine(Path.GetTempPath(), $"git-retry-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(path, harness.Replace("\r\n", "\n"));
        try
        {
            var bash = BashExecutable();
            var startInfo = new System.Diagnostics.ProcessStartInfo(bash)
            {
                ArgumentList = { path },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // A non-login Git for Windows bash started from PowerShell does not put its own tools (mktemp) on PATH.
            if (Path.IsPathRooted(bash))
                startInfo.Environment["PATH"] = Path.GetDirectoryName(bash) + Path.PathSeparator + startInfo.Environment["PATH"];
            using var process = System.Diagnostics.Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RewritePwshAndChains_NoChain_ReturnsUnchanged()
    {
        const string cmd = "dotnet build";
        Assert.Equal(cmd, PipelineCommandBuilder.RewritePwshAndChains(cmd));
    }

    [Fact]
    public void RewritePwshAndChains_TopLevelChain_InsertsExitCodeGuard()
    {
        var result = PipelineCommandBuilder.RewritePwshAndChains("dotnet build && dotnet test");

        Assert.Equal($"dotnet build {Guard} dotnet test", result);
        Assert.DoesNotContain("&&", result);
    }

    [Fact]
    public void RewritePwshAndChains_InsideSingleQuotes_NotRewritten()
    {
        const string cmd = "Write-Output 'a && b'";
        Assert.Equal(cmd, PipelineCommandBuilder.RewritePwshAndChains(cmd));
    }

    [Fact]
    public void RewritePwshAndChains_InsideDoubleQuotes_NotRewritten()
    {
        const string cmd = "Write-Output \"a && b\"";
        Assert.Equal(cmd, PipelineCommandBuilder.RewritePwshAndChains(cmd));
    }

    [Fact]
    public void RewritePwshAndChains_BacktickEscapedQuote_DoesNotDesyncString()
    {
        // The `" is an escaped quote inside the double-quoted string, so the && stays inside the
        // literal and must NOT be rewritten. The naive scanner flipped the string state here.
        const string cmd = "Write-Output \"quote: `\" && still inside\"";
        Assert.Equal(cmd, PipelineCommandBuilder.RewritePwshAndChains(cmd));
    }

    [Fact]
    public void RewritePwshAndChains_DoubledSingleQuoteLiteral_KeepsChainInsideString()
    {
        // 'it''s && fine' is a single PowerShell string containing &&.
        const string cmd = "Write-Output 'it''s && fine'";
        Assert.Equal(cmd, PipelineCommandBuilder.RewritePwshAndChains(cmd));
    }

    [Fact]
    public void RewritePwshAndChains_ChainAfterQuotedLiteral_IsRewritten()
    {
        var result = PipelineCommandBuilder.RewritePwshAndChains("echo 'a && b' && dotnet test");

        // The quoted && is preserved; only the top-level one becomes a guard.
        Assert.Contains("'a && b'", result);
        Assert.Contains(Guard, result);
        Assert.Equal($"echo 'a && b' {Guard} dotnet test", result);
    }

    [Fact]
    public void BuildStepCommand_Windows_RewritesChainAndPrependsErrorAction()
    {
        var step = new Aetheus.Shared.Components.Pipelines.PipelineStepDefinition
        {
            Name = "build",
            Shell = "dotnet build && dotnet test"
        };

        var result = PipelineCommandBuilder.BuildStepCommand(step, [], isWindows: true);

        Assert.StartsWith("$ErrorActionPreference = 'Stop'", result);
        Assert.Contains(Guard, result);
        Assert.DoesNotContain("&&", result);
    }

    [Fact]
    public void BuildStepCommand_Linux_LeavesChainNative()
    {
        var step = new Aetheus.Shared.Components.Pipelines.PipelineStepDefinition
        {
            Name = "build",
            Shell = "dotnet build && dotnet test"
        };

        var result = PipelineCommandBuilder.BuildStepCommand(step, [], isWindows: false);

        Assert.StartsWith("set -e", result);
        Assert.Contains("dotnet build && dotnet test", result);
    }

    [Fact]
    public void BuildStepCommand_SecretVariable_PersistsOnlyOpaquePlaceholder()
    {
        var step = new Aetheus.Shared.Components.Pipelines.PipelineStepDefinition
        {
            Name = "deploy",
            Shell = "deploy --token '$(DEPLOY_TOKEN)'"
        };
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DEPLOY_TOKEN"] = "super-secret-value"
        };

        var result = PipelineCommandBuilder.BuildStepCommand(
            step, variables, isWindows: false, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DEPLOY_TOKEN" });

        Assert.DoesNotContain("super-secret-value", result, StringComparison.Ordinal);
        Assert.Contains("__AETHEUS_SECRET_", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStepCommand_SecretInRejectedWorkingDirectory_IsNotPersisted()
    {
        var step = new Aetheus.Shared.Components.Pipelines.PipelineStepDefinition
        {
            Name = "deploy",
            Shell = "deploy",
            WorkingDirectory = "$(SECRET_PATH)"
        };
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WORKSPACE"] = "/srv/aetheus/workspace",
            ["SECRET_PATH"] = "/outside/super-secret-value"
        };

        var result = PipelineCommandBuilder.BuildStepCommand(
            step, variables, isWindows: false, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SECRET_PATH" });

        Assert.DoesNotContain("super-secret-value", result, StringComparison.Ordinal);
        Assert.Contains("working_directory is outside", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/tmp/work/s", "/tmp/work/s", false)]
    [InlineData("/tmp/work/s/src", "/tmp/work/s", false)]
    [InlineData("/tmp/work/sibling", "/tmp/work/s", true)]
    [InlineData("/tmp/work/s/../escape", "/tmp/work/s", true)]
    [InlineData(@"C:\w\slot\s", @"C:\w\slot\s", false)]
    [InlineData(@"C:\w\slot\s\src", @"C:\w\slot\s", false)]
    [InlineData(@"C:\w\slot\sibling", @"C:\w\slot\s", true)]
    [InlineData(@"C:\w\slot\s\..\escape", @"C:\w\slot\s", true)]
    public void WorkingDirectoryEscapesWorkspace_UsesPathSegments(
        string workDir, string workspace, bool expected)
    {
        Assert.Equal(expected, PipelineCommandBuilder.WorkingDirectoryEscapesWorkspace(workDir, workspace));
    }

    [Fact]
    public void BuildStepCommand_Linux_CheckoutStep_CdsIntoWorkspaceWithoutRecloning()
    {
        // 0-d regression: the system Prepare step already cloned the repo, so a `checkout: true`
        // step must NOT clone a second time - it only cd's into the already-checked-out workspace.
        var step = new Aetheus.Shared.Components.Pipelines.PipelineStepDefinition
        {
            Name = "build",
            Checkout = true,
            Shell = "dotnet build"
        };
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["REPOSITORY_URL"] = "https://example.com/repo.git",
            ["WORKSPACE"] = "/tmp/ws/s"
        };

        var result = PipelineCommandBuilder.BuildStepCommand(step, vars, isWindows: false);

        Assert.Contains("cd \"/tmp/ws/s\"", result);
        Assert.Contains("dotnet build", result);
        Assert.DoesNotContain("git clone", result);
        Assert.DoesNotContain("git fetch", result);
    }

    [Fact]
    public void BuildStepCommand_Windows_CheckoutStep_SetsLocationWithoutRecloning()
    {
        var step = new Aetheus.Shared.Components.Pipelines.PipelineStepDefinition
        {
            Name = "build",
            Checkout = true,
            Shell = "dotnet build"
        };
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["REPOSITORY_URL"] = "https://example.com/repo.git",
            ["WORKSPACE"] = @"C:\w\s"
        };

        var result = PipelineCommandBuilder.BuildStepCommand(step, vars, isWindows: true);

        Assert.Contains(@"New-Item -ItemType Directory -Force -Path 'C:\w\s'", result);
        Assert.DoesNotContain("-LiteralPath", result);
        Assert.Contains(@"Set-Location 'C:\w\s'", result);
        Assert.Contains("dotnet build", result);
        Assert.DoesNotContain("git clone", result);
        Assert.DoesNotContain("git fetch", result);
    }

    [Fact]
    public void BuildStepCommand_Linux_NonCheckoutStep_StillRunsInsideWorkspace()
    {
        var step = new Aetheus.Shared.Components.Pipelines.PipelineStepDefinition
        {
            Name = "test",
            Checkout = false,
            Shell = "dotnet test"
        };
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WORKSPACE"] = "/tmp/aetheus/runs/42/s"
        };

        var result = PipelineCommandBuilder.BuildStepCommand(step, vars, isWindows: false);

        // The preamble now opens with the lost-workspace guard, so the entry sequence is asserted
        // in place rather than at position 0. What matters is unchanged: the step enters its
        // workspace, and it does so before running anything of its own.
        Assert.StartsWith("set -e\nif [ ! -d ", result, StringComparison.Ordinal);
        Assert.Contains(
            "mkdir -p -- \"/tmp/aetheus/runs/42/s\"\ncd \"/tmp/aetheus/runs/42/s\"\n",
            result,
            StringComparison.Ordinal);
        Assert.Contains("dotnet test", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// The preamble recreates a vanished workspace so an always() teardown can still run, which on its
    /// own turns "the agent restarted and took the sources with it" into "no such file" on whatever the
    /// step happens to open first. Production run 2185 read as a missing repository script for that
    /// reason. The guard has to name the real cause, and must not fail the step by itself.
    /// </summary>
    [Fact]
    public void BuildStepCommand_Linux_WarnsWhenTheRunWorkspaceHadToBeRecreated()
    {
        var step = new Aetheus.Shared.Components.Pipelines.PipelineStepDefinition
        {
            Name = "test",
            Shell = "dotnet test"
        };
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WORKSPACE"] = "/tmp/aetheus/runs/42/s"
        };

        var result = PipelineCommandBuilder.BuildStepCommand(step, vars, isWindows: false);

        Assert.Contains(
            "if [ ! -d \"/tmp/aetheus/runs/42/s\" ]; then echo ",
            result,
            StringComparison.Ordinal);
        Assert.Contains("recreated empty", result, StringComparison.Ordinal);
        Assert.Contains("the agent restarted", result, StringComparison.Ordinal);
        // Diagnostic only: it reports on stderr and leaves the exit status alone, so teardown still runs.
        Assert.Contains(">&2; fi", result, StringComparison.Ordinal);
        Assert.DoesNotContain("exit 1", result, StringComparison.Ordinal);
        // The guard runs before the step enters the directory it is describing.
        Assert.True(
            result.IndexOf("if [ ! -d ", StringComparison.Ordinal)
            < result.IndexOf("mkdir -p --", StringComparison.Ordinal));
    }

    /// <summary>Windows keeps a durable C:\\w workspace, but the same guard applies to a lost directory.</summary>
    [Fact]
    public void BuildStepCommand_Windows_WarnsWhenTheRunWorkspaceHadToBeRecreated()
    {
        var step = new Aetheus.Shared.Components.Pipelines.PipelineStepDefinition
        {
            Name = "build",
            Shell = "dotnet build"
        };
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WORKSPACE"] = @"C:\w\s"
        };

        var result = PipelineCommandBuilder.BuildStepCommand(step, vars, isWindows: true);

        Assert.Contains("Write-Warning", result, StringComparison.Ordinal);
        Assert.Contains("recreated empty", result, StringComparison.Ordinal);
        Assert.True(
            result.IndexOf("Write-Warning", StringComparison.Ordinal)
            < result.IndexOf("New-Item", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildStepCommand_Linux_RecreatesValidatedWorkspaceBeforeAlwaysTeardown()
    {
        var step = new Aetheus.Shared.Components.Pipelines.PipelineStepDefinition
        {
            Name = "Destroy QA stack",
            Shell = "docker ps --filter label=com.docker.compose.project=aetheus-qa-42",
            WorkingDirectory = "$(WORKSPACE)"
        };
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WORKSPACE"] = "/tmp/aetheus/runs/42/s"
        };

        var result = PipelineCommandBuilder.BuildStepCommand(step, vars, isWindows: false);

        Assert.Contains(
            "mkdir -p -- \"/tmp/aetheus/runs/42/s\"\ncd \"/tmp/aetheus/runs/42/s\"\n",
            result,
            StringComparison.Ordinal);
        Assert.True(
            result.IndexOf("mkdir -p --", StringComparison.Ordinal)
            < result.IndexOf("docker ps --filter", StringComparison.Ordinal));
        Assert.DoesNotContain("cd \"/\"", result, StringComparison.Ordinal);
    }

    [PlatformFact("linux")]
    public void BuildStepCommand_Linux_ExecutesAfterWorkspaceWasCompletelyRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aetheus-command-builder-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "s");
        var proof = Path.Combine(workspace, "teardown-reached");
        var step = new Aetheus.Shared.Components.Pipelines.PipelineStepDefinition
        {
            Name = "Destroy QA stack",
            Shell = $"test -d \"{workspace}\" && printf reached > \"{proof}\"",
            WorkingDirectory = "$(WORKSPACE)"
        };
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WORKSPACE"] = workspace
        };

        try
        {
            var command = PipelineCommandBuilder.BuildStepCommand(step, variables, isWindows: false);
            var startInfo = new System.Diagnostics.ProcessStartInfo("/bin/bash");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
            using var process = System.Diagnostics.Process.Start(startInfo);
            Assert.NotNull(process);
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("reached", File.ReadAllText(proof));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildStepCommand_ExplicitWorkingDirectory_OverridesRunWorkspace()
    {
        var step = new Aetheus.Shared.Components.Pipelines.PipelineStepDefinition
        {
            Name = "publish",
            Shell = "dotnet publish",
            WorkingDirectory = "$(WORKSPACE)/src/App"
        };
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WORKSPACE"] = "/tmp/aetheus/runs/43/s"
        };

        var result = PipelineCommandBuilder.BuildStepCommand(step, vars, isWindows: false);

        // The preamble now opens with the lost-workspace guard, so the entry sequence is asserted
        // in place rather than at position 0. What matters is unchanged: the step enters its
        // workspace, and it does so before running anything of its own.
        Assert.StartsWith("set -e\nif [ ! -d ", result, StringComparison.Ordinal);
        Assert.Contains(
            "mkdir -p -- \"/tmp/aetheus/runs/43/s/src/App\"\ncd \"/tmp/aetheus/runs/43/s/src/App\"\n",
            result,
            StringComparison.Ordinal);
    }

    // --- S-TECH-V6QN: ${{ parameters.X }} namespace ---

    [Fact]
    public void Substitute_ParameterNamespace_ResolvesFromParameterKey()
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["parameters.env"] = "prod"
        };
        Assert.Equal("deploy to prod", PipelineCommandBuilder.Substitute("deploy to ${{ parameters.env }}", vars));
    }

    [Fact]
    public void Substitute_ParameterNamespace_NotShadowedBySameNamedVariable()
    {
        // A run parameter and a YAML variable can share a name without colliding: $(env) and
        // ${{ parameters.env }} resolve from separate namespaces.
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["env"] = "from-variable",
            ["parameters.env"] = "from-parameter"
        };
        Assert.Equal("from-parameter / from-variable",
            PipelineCommandBuilder.Substitute("${{ parameters.env }} / $(env)", vars));
    }

    [Fact]
    public void Substitute_ParameterNamespace_UnknownParameter_LeftLiteral()
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x"] = "y" };
        Assert.Equal("${{ parameters.missing }}", PipelineCommandBuilder.Substitute("${{ parameters.missing }}", vars));
    }

    [Fact]
    public void Substitute_QualifiedStepOutput_ResolvesDottedVariableName()
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NoneCandidate.None.CANDIDATE_VERSION"] = "c-deadbeef"
        };

        Assert.Equal(
            "c-deadbeef",
            PipelineCommandBuilder.Substitute("$(NoneCandidate.None.CANDIDATE_VERSION)", vars));
    }

    // --- D-03: $(NAME:-default) ---

    /// <summary>
    /// Before the default syntax existed, <c>$(A:-B)</c> matched nothing, stayed literal, and reached
    /// the shell as a POSIX command substitution running a command called <c>A:-B</c>.
    /// </summary>
    [Theory]
    [InlineData("v$(APP_VERSION:-0.0.0)", "v0.0.0")]
    [InlineData("[$(APP_VERSION:-)]", "[]")]
    [InlineData("$(EMPTY:-fallback)", "fallback")]
    [InlineData("$(DEFINED:-fallback)", "1.2.3")]
    [InlineData("$(DEFINED)-$(MISSING:-x)-$(MISSING)", "1.2.3-x-$(MISSING)")]
    public void Substitute_DefaultApplies_WhenTheNameIsMissingOrEmpty(string text, string expected)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DEFINED"] = "1.2.3",
            ["EMPTY"] = ""
        };

        Assert.Equal(expected, PipelineCommandBuilder.Substitute(text, vars));
    }

    [Fact]
    public void Substitute_DefaultApplies_EvenWithNoVariablesAtAll()
        => Assert.Equal("echo 0", PipelineCommandBuilder.Substitute("echo $(COUNTER:-0)", new Dictionary<string, string>()));

    [Fact]
    public void Substitute_ADefaultCannotNest()
    {
        // `$(A:-$(B))` is not supported: refusing parentheses in the default keeps the outer
        // reference from matching half of itself, so the inner one alone resolves.
        var vars = new Dictionary<string, string> { ["B"] = "b" };

        Assert.Equal("$(A:-b)", PipelineCommandBuilder.Substitute("$(A:-$(B))", vars));
    }

    [Fact]
    public void Substitute_PendingDefault_StaysLiteralUntilTheNameCanNoLongerArrive()
    {
        var vars = new Dictionary<string, string>();

        Assert.Equal(
            "$(SOURCE_COMMIT:-none)",
            PipelineCommandBuilder.Substitute("$(SOURCE_COMMIT:-none)", vars, name => name != "SOURCE_COMMIT"));
        Assert.Equal(
            "$(SOURCE_COMMIT:-none)",
            PipelineCommandBuilder.Substitute("$(SOURCE_COMMIT:-none)", vars, defaultApplies: null));
    }

    [Fact]
    public void Substitute_ShellCommandSubstitution_IsStillLeftAlone()
    {
        var vars = new Dictionary<string, string> { ["date"] = "should-not-apply" };

        Assert.Equal("$(date -u +%s) $(pwd)", PipelineCommandBuilder.Substitute("$(date -u +%s) $(pwd)", vars));
    }
}
