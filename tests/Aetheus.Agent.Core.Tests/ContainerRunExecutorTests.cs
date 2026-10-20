// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Toolchains;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Covers the pre-flight validation branches of <see cref="ContainerRunExecutor"/> that return
/// before any <c>docker</c> process is spawned - so they are exercisable without a Docker daemon.
/// </summary>
public class ContainerRunExecutorTests
{
    private static readonly string ImmutableImage =
        $"debian:bookworm-slim@sha256:{new string('a', 64)}";

    private static ContainerRunExecutor NewExecutor() =>
        new(
            Options.Create(new AetheusAgentOptions()),
            NullLogger<ContainerRunExecutor>.Instance,
            new ToolchainResolver(),
            TimeProvider.System);

    [Fact]
    public void ResolveHostWorkspace_UsesOnlyExactRunWorkspace()
    {
        const int runId = 791;
        var exec = NewExecutor();
        var spec = new ContainerSpec { WorkspaceKey = runId };
        var slot = unchecked((uint)runId * 2654435761u).ToString("x8");
        // Spelled out rather than read from HostWorkspaceResolver: the point is to assert the agreed
        // path independently, so a change to the production formula fails here instead of following
        // it. Linux left the OS temp directory in 544b409e (systemd PrivateTmp destroyed live
        // workspaces); this expectation had stayed on the old one and failed on every Linux agent.
        var expected = OperatingSystem.IsWindows()
            ? Path.Combine(@"C:\w", slot, "s")
            : Path.Combine("/var/lib/aetheus-agent", "w", slot, "s");

        var accepted = exec.ResolveHostWorkspace(
            spec,
            new Dictionary<string, string>
            {
                [ContainerRunExecutor.HostWorkspaceVariable] = expected
            });
        var rejected = exec.ResolveHostWorkspace(
            spec,
            new Dictionary<string, string>
            {
                [ContainerRunExecutor.HostWorkspaceVariable] = Path.GetTempPath()
            });

        Assert.Equal(Path.GetFullPath(expected), accepted);
        Assert.NotEqual(Path.GetFullPath(Path.GetTempPath()), rejected);
        Assert.EndsWith(Path.Combine("cw", runId.ToString()), rejected, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainerWorkspaceContract_RefreshesBeforeAndPublishesAfterEachStep()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "Aetheus.Agent.Core",
            "Executors",
            "ContainerWorkspaceMaterializer.cs"));

        Assert.Contains("Refresh before every container", source, StringComparison.Ordinal);
        Assert.Contains("SynchronizeBackAsync", source, StringComparison.Ordinal);
        Assert.Contains("Could not publish container outputs", source, StringComparison.Ordinal);
    }

    private static async Task<(ExecutorResult Result, List<string> Errors)> RunAsync(ContainerSpec spec)
    {
        var errors = new List<string>();
        Task Sink(string line, TaskLogLevel level)
        {
            if (level == TaskLogLevel.Error) errors.Add(line);
            return Task.CompletedTask;
        }

        var result = await NewExecutor().ExecuteAsync(spec, "echo hi", new Dictionary<string, string>(), 60, Sink, TestContext.Current.CancellationToken);
        return (result, errors);
    }

    [Fact]
    public async Task ExecuteAsync_NoImmutableImage_BlocksWithoutSpawningDocker()
    {
        var (result, errors) = await RunAsync(new ContainerSpec { Image = "", WorkspaceKey = 1 });

        Assert.Equal(ToolchainResolution.InfrastructureMismatchExitCode, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(TaskFailureCodes.InfrastructureMismatch, result.FailureCode);
        Assert.Contains(errors, e => e.Contains("immutable", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("evil")]
    [InlineData("docker")]
    [InlineData("../runsc")]
    public async Task ExecuteAsync_DisallowedRuntime_Blocks(string runtime)
    {
        var (result, errors) = await RunAsync(new ContainerSpec { Image = ImmutableImage, Runtime = runtime, WorkspaceKey = 1 });

        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains(errors, e => e.Contains("not allowed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_InvalidMultilineSecretKey_FailsInsteadOfDroppingSecret()
    {
        var errors = new List<string>();
        Task Sink(string line, TaskLogLevel level)
        {
            if (level == TaskLogLevel.Error) errors.Add(line);
            return Task.CompletedTask;
        }

        var result = await NewExecutor().ExecuteAsync(
            new ContainerSpec { Image = ImmutableImage, WorkspaceKey = 1 },
            "echo hi",
            new Dictionary<string, string> { ["invalid key"] = "line 1\nline 2" },
            60,
            Sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains(errors, error =>
            error.Contains("invalid key", StringComparison.Ordinal)
            && error.Contains("cannot be delivered securely", StringComparison.OrdinalIgnoreCase));
    }

    // --- S-TECH-Q9MF: multi-line secrets via a mounted file, never on argv ---

    [Theory]
    [InlineData("PEM_KEY", true)]
    [InlineData("_x", true)]
    [InlineData("A1_B2", true)]
    [InlineData("1BAD", false)]     // leading digit
    [InlineData("has space", false)]
    [InlineData("has=eq", false)]
    [InlineData("", false)]
    public void IsValidShellKey_AcceptsOnlyPosixIdentifiers(string key, bool expected) =>
        Assert.Equal(expected, ContainerRunExecutor.IsValidShellKey(key));

    [Fact]
    public async Task WriteSecretsFileLines_EmitsSingleQuoteEscapedExports()
    {
        var path = Path.Combine(Path.GetTempPath(), $"secrets-test-{Guid.NewGuid():N}.sh");
        try
        {
            var secrets = new List<KeyValuePair<string, string>>
            {
                new("PEM", "-----BEGIN-----\nline1\nline2\n-----END-----"),
                new("QUOTED", "a'b"),
            };
            await ContainerRunExecutor.WriteSecretsFileLinesAsync(secrets, path, TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            // Multi-line value survives verbatim inside the single-quoted export (newlines preserved).
            Assert.Contains("export PEM='-----BEGIN-----\nline1\nline2\n-----END-----'", content, StringComparison.Ordinal);
            // A single quote in the value is escaped as '\'' so the export stays well-formed.
            Assert.Contains("export QUOTED='a'\\''b'", content, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClassifySecrets_LeavesAStageQualifiedOutputOutOfTheContainerEnvironment()
    {
        // Mirror quality run 1282: docker refused the env file over
        // "Toolchain.Resolve source languages.HAS_DOTNET", so no container step of the run could start.
        var secrets = ContainerRunExecutor.ClassifySecrets(new Dictionary<string, string>
        {
            ["HAS_DOTNET"] = "true",
            ["Toolchain.Resolve source languages.HAS_DOTNET"] = "true",
            ["Build.Compile the app.NOTES"] = "a\nb",
            ["invalid key"] = "line 1\nline 2",
        });

        Assert.Equal(["HAS_DOTNET"], secrets.Environment.Select(kv => kv.Key));
        Assert.Empty(secrets.Mounted);
        // Any other unusable key still fails the step rather than vanishing.
        Assert.Equal(["invalid key"], secrets.InvalidKeys);
    }

    [Fact]
    public void ResolveSecretFilePaths_StayUnderDaemonVisibleStateRoot_NeverTheServicePrivateTmp()
    {
        var stateRoot = Path.Combine(Path.GetTempPath(), "agent-work", "container-state", "7");

        var (envFile, secretsFile) = ContainerRunExecutor.ResolveSecretFilePaths(
            stateRoot, "prom-x", hasEnvironment: true, hasMounted: true);

        var secretsDirectory = Path.Combine(stateRoot, "secrets");
        Assert.Equal(Path.Combine(secretsDirectory, "prom-x.env"), envFile);
        Assert.Equal(Path.Combine(secretsDirectory, "prom-x.secrets.sh"), secretsFile);
        Assert.NotEqual(
            Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
            Path.GetDirectoryName(secretsFile));
        Assert.Equal(
            (null, null),
            ContainerRunExecutor.ResolveSecretFilePaths(stateRoot, "prom-x", hasEnvironment: false, hasMounted: false));
    }

    [Fact]
    public void BuildDockerRun_MultiLineSecrets_UseMountAndSource_NeverArgv()
    {
        var exec = NewExecutor();
        var resolution = new ToolchainResolution
        {
            IsSuccess = true,
            Image = ImmutableImage,
            Shell = "bash"
        };
        var psi = exec.BuildDockerRun(
            new ContainerSpec { Image = ImmutableImage, WorkspaceKey = 1 },
            resolution,
            "/host/ws",
            "/host/home",
            [new KeyValuePair<string, string>("/host/cache", "/home/aetheus/.cache")],
            ".prom-step-00000000000000000000000000000000.sh",
            "prom-x",
            null,
            "/tmp/prom-x.secrets.sh");
        var args = psi.ArgumentList;

        // No inline -e: a secret value can never land on the world-readable /proc/<pid>/cmdline.
        Assert.DoesNotContain("-e", args);
        // The secrets file is bind-mounted read-only and sourced before the step script runs.
        Assert.Contains(args, a => a.Contains(".secrets.sh:", StringComparison.Ordinal) && a.EndsWith(":ro", StringComparison.Ordinal));
        Assert.Contains(args, a => a.StartsWith(". /run/aetheus/", StringComparison.Ordinal)
            && a.Contains("exec bash /w/.prom-step-00000000000000000000000000000000.sh", StringComparison.Ordinal));
        Assert.Contains("--read-only", args);
        Assert.Contains("/tmp:rw,nosuid,nodev,noexec,size=512m", args);
        Assert.Contains("--user", args);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(
                "/home/aetheus:rw,nosuid,nodev,uid=65532,gid=65532,mode=0700,size=512m",
                args);
            Assert.DoesNotContain("/host/home:/home/aetheus", args);
            Assert.DoesNotContain("/host/cache:/home/aetheus/.cache", args);
        }
        else
        {
            Assert.Contains("/host/home:/home/aetheus", args);
            Assert.Contains("/host/cache:/home/aetheus/.cache", args);
        }
        Assert.Contains(ImmutableImage, args);
    }

    [Fact]
    public void GeneratedScriptNames_AreShellSafeTokens()
    {
        var names = Enumerable.Range(0, 100)
            .Select(static _ => ContainerRunExecutor.CreateScriptName())
            .ToArray();

        Assert.All(names, name => Assert.True(ContainerRunExecutor.IsShellSafeScriptName(name), name));
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(".prom-step-safe name.sh")]
    [InlineData(".prom-step-name;id.sh")]
    [InlineData("../../step.sh")]
    [InlineData(".prom-step-$(id).sh")]
    public void BuildDockerRun_RejectsUnsafeScriptName(string scriptName)
    {
        var exec = NewExecutor();
        var resolution = new ToolchainResolution
        {
            IsSuccess = true,
            Image = ImmutableImage,
            Shell = "bash"
        };

        Assert.Throws<ArgumentException>(() => exec.BuildDockerRun(
            new ContainerSpec { Image = ImmutableImage, WorkspaceKey = 1 },
            resolution,
            "/host/ws",
            "/host/home",
            [],
            scriptName,
            "prom-x",
            null,
            "/tmp/prom-x.secrets.sh"));
    }

    [Fact]
    public void BuildDockerRun_BridgeNetwork_ForwardsResolvedLocalBackendAuthority()
    {
        var exec = NewExecutor();
        var resolution = new ToolchainResolution
        {
            IsSuccess = true,
            Image = ImmutableImage,
            Shell = "bash"
        };

        var bridge = exec.BuildDockerRun(
            new ContainerSpec { Image = ImmutableImage, WorkspaceKey = 1, Network = "bridge" },
            resolution,
            "/host/ws",
            "/host/home",
            [],
            ".prom-step-00000000000000000000000000000000.sh",
            "prom-bridge",
            null,
            null,
            "192.0.2.10").ArgumentList;
        var isolated = exec.BuildDockerRun(
            new ContainerSpec { Image = ImmutableImage, WorkspaceKey = 2, Network = "none" },
            resolution,
            "/host/ws",
            "/host/home",
            [],
            ".prom-step-00000000000000000000000000000000.sh",
            "prom-none",
            null,
            null,
            "192.0.2.10").ArgumentList;

        Assert.Contains("host.docker.internal:192.0.2.10", bridge);
        Assert.Contains("runner.host.internal:host-gateway", bridge);
        Assert.DoesNotContain("host.docker.internal:192.0.2.10", isolated);
        Assert.DoesNotContain("runner.host.internal:host-gateway", isolated);
    }

    [Theory]
    [InlineData("/workspace/.git", true)]
    [InlineData("/workspace/.aetheus-container-workspace-ready", true)]
    [InlineData("/workspace/.aetheus-injection", false)]
    [InlineData("/workspace/.prom-step-0123456789abcdef0123456789abcdef.sh", true)]
    [InlineData("/workspace/.prom-step-notaguid.sh", false)]
    [InlineData("/workspace/src", false)]
    public void IsProtectedSyncEntry_ExcludesOnlyRunnerMetadataAndStepScripts(string entry, bool expected) =>
        Assert.Equal(expected, ContainerWorkspaceMaterializer.IsProtectedSyncEntry(entry));

    [Fact]
    public void BuildDockerRun_ReadOnlyMounts_AreBoundReadOnlyBesideTheWorkspace()
    {
        var resolution = new ToolchainResolution { IsSuccess = true, Image = ImmutableImage, Shell = "sh" };

        var args = NewExecutor().BuildDockerRun(
            new ContainerSpec { Image = ImmutableImage, WorkspaceKey = 1 },
            resolution,
            "/host/ws",
            "/host/home",
            [],
            ".prom-step-00000000000000000000000000000000.sh",
            "prom-fn",
            null,
            null,
            readOnlyMounts: [new ContainerReadOnlyMount("/opt/aetheus-agent/aetheus-fn", "/opt/aetheus-fn")]).ArgumentList;

        var mount = args.IndexOf("/opt/aetheus-agent/aetheus-fn:/opt/aetheus-fn:ro");
        Assert.True(mount > 0, string.Join(' ', args));
        Assert.Equal("-v", args[mount - 1]);
        Assert.Contains("/host/ws:/w", args);
        // The hardening stays as for any step: the mount adds nothing writable.
        Assert.Contains("--read-only", args);
        Assert.Contains("ALL", args);
        Assert.True(mount < args.IndexOf("--"), "The mount must be a docker option, before the image.");
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/w")]
    [InlineData("/w/tools")]
    [InlineData("/opt/a:b")]
    [InlineData("/opt/a,b")]
    [InlineData("/")]
    public void BuildDockerRun_RefusesAnInvalidReadOnlyMount(string containerPath)
    {
        var resolution = new ToolchainResolution { IsSuccess = true, Image = ImmutableImage, Shell = "sh" };

        Assert.Throws<ArgumentException>(() => NewExecutor().BuildDockerRun(
            new ContainerSpec { Image = ImmutableImage, WorkspaceKey = 1 },
            resolution,
            "/host/ws",
            "/host/home",
            [],
            ".prom-step-00000000000000000000000000000000.sh",
            "prom-fn",
            null,
            null,
            readOnlyMounts: [new ContainerReadOnlyMount("/host/fn", containerPath)]));
    }

    [Fact]
    public async Task ExecuteAsync_InvalidReadOnlyMount_IsBlockedBeforeAnyContainerStarts()
    {
        var errors = new List<string>();

        var result = await NewExecutor().ExecuteAsync(
            new ContainerSpec { Image = ImmutableImage, WorkspaceKey = 1 },
            "echo hi",
            new Dictionary<string, string>(),
            60,
            (line, level) =>
            {
                if (level == TaskLogLevel.Error) errors.Add(line);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken,
            [new ContainerReadOnlyMount("/host/fn", "/w")]);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(errors, line => line.Contains("Read-only container mount '/w' is invalid", StringComparison.Ordinal));
    }

    private static string FindRepoRoot() => Aetheus.Agent.Core.Tests.RepositoryScan.Root;
}
