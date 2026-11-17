// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-37 and R-39: the fast release the setup wizard generates (<c>application-release-fast</c>) really runs on
/// the exact revision of the run and fails, by name, when it cannot. These tests do not read the template's text: they
/// run the shell the template ships, with a real <c>sh</c>, a real Git repository and a recording stand-in for
/// <c>docker</c>, and observe what it does. What they do not prove is a Docker Compose start on a server (the stand-in
/// answers every <c>docker</c> call): that is the real run of a generated fast release.
/// </summary>
public sealed class ReleaseFastTemplateRevisionTests : IDisposable
{
    private const string Template = "generic/application-release-fast.yaml";
    private const string ComposePath = "deploy/compose/remote.compose.yml";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "release-fast-" + Guid.NewGuid().ToString("N"));
    private readonly string _repository;
    private readonly string _binaries;
    private readonly string _deployDirectory;

    public ReleaseFastTemplateRevisionTests()
    {
        _repository = Path.Combine(_root, "repo");
        _binaries = Path.Combine(_root, "bin");
        _deployDirectory = Path.Combine(_root, "deploy");
        Directory.CreateDirectory(_repository);
        Directory.CreateDirectory(_binaries);
        // Each call is recorded in the test's own folder (the release is built in a directory the test does not know in
        // advance, so a file in the working directory would be lost with it).
        var fakeDocker = Path.Combine(_binaries, "docker");
        File.WriteAllText(fakeDocker,
            $"#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"{_root.Replace('\\', '/')}/docker-calls\"\nexit 0\n", new UTF8Encoding(false));
        // A file written on Linux is not executable: `command -v docker` then skips the stand-in and finds the host's real docker.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fakeDocker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a Windows file lock on a temp folder is not a test result */ }
        catch (UnauthorizedAccessException) { /* idem */ }
    }

    /// <summary>The Checkout stage's only step, as the template ships it.</summary>
    private static PipelineStepDefinition CheckoutStep()
    {
        var definition = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(
            DeliveryTemplateCatalog.ReadResource(Template))!;
        var stage = Assert.Single(YamlParsingHelper.FlattenJobs(definition), item => item.Name == "Checkout");
        return Assert.Single(stage.Steps);
    }

    private static string FastReleaseCommands() =>
        YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(
            DeliveryTemplateCatalog.ReadResource(Template))!.Variables["APPLICATION_RELEASE_FAST_COMMANDS"];

    private static string PosixShell()
    {
        foreach (var candidate in new[] { "/bin/sh", "/usr/bin/sh", @"C:\Program Files\Git\usr\bin\sh.exe", @"C:\Program Files\Git\bin\sh.exe" })
            if (File.Exists(candidate)) return candidate;
        throw new InvalidOperationException("No POSIX sh was found: the shell the template ships cannot be run.");
    }

    private static (int ExitCode, string Output) Run(
        string fileName, IEnumerable<string> arguments, string workingDirectory, IDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>()) start.Environment[key] = value;
        using var process = Process.Start(start)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), $"{fileName} did not end.");
        return (process.ExitCode, standardOutput.Result + standardError.Result);
    }

    private string Git(params string[] arguments)
    {
        var (code, output) = Run("git",
            ["-c", "user.name=test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", .. arguments], _repository);
        Assert.True(code == 0, $"git {string.Join(' ', arguments)} failed: {output}");
        return output.Trim();
    }

    /// <summary>A Git repository with one commit holding the Compose file at <see cref="ComposePath"/>, and its commit.</summary>
    private string CommitRepository(string composePath = ComposePath)
    {
        Git("init", "-q");
        var file = Path.Combine(_repository, composePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "services: {}\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "init");
        return Git("rev-parse", "HEAD");
    }

    private (int ExitCode, string Output) RunShell(string script, Dictionary<string, string> environment)
    {
        // The shell's own directory comes next: on a Windows host, Git's sh finds mkdir, sed and tar beside it, which the
        // PATH of a PowerShell host does not carry.
        var shell = PosixShell();
        environment["PATH"] = _binaries + Path.PathSeparator + Path.GetDirectoryName(shell) + Path.PathSeparator
            + Environment.GetEnvironmentVariable("PATH");
        // A file, not `-c`: the fast release script is longer than the argument length a Windows host hands to its Git sh
        // (it silently cut the script after about 8 KB and exited 0).
        var scriptFile = Path.Combine(_root, "script-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(scriptFile, script, new UTF8Encoding(false));
        return Run(shell, ["-eu", scriptFile], _repository, environment);
    }

    /// <summary>
    /// The script of the step that runs the commands, reduced to what the platform does before it: a rendered environment
    /// file at an absolute path in the workspace, handed over as DEPLOY_ENV_FILE (the step's own rendering is the
    /// platform's <c>env_template</c>, which a unit test cannot run).
    /// </summary>
    private static string WithRenderedEnvironmentFile(string commands) =>
        "printf 'DB_PASSWORD=secret\\n' > .deploy-env.test\n"
        + "DEPLOY_ENV_FILE=\"$PWD/.deploy-env.test\"\n"
        + "export DEPLOY_ENV_FILE\n"
        + commands;

    private string DockerCalls()
    {
        var calls = Path.Combine(_root, "docker-calls");
        return File.Exists(calls) ? File.ReadAllText(calls) : string.Empty;
    }

    private Dictionary<string, string> FastEnvironment(string revision, string? composeFile = ComposePath)
    {
        var environment = new Dictionary<string, string>
        {
            ["BUILD_SOURCEVERSION"] = revision,
            ["COMPOSE_PROJECT"] = "atlas",
            ["DEPLOY_DIR"] = "../deploy"
        };
        if (composeFile is not null) environment["COMPOSE_FILE"] = composeFile;
        return environment;
    }

    // R-39: the step called "Check out the exact revision" used to be `checkout: true` alone, which runs nothing and
    // succeeded under a name that announced work. Now it has a command, and the command is a real verification.

    [Fact]
    public void TheCheckoutStep_HasACommand_AndSucceedsOnTheExactRevision()
    {
        var revision = CommitRepository();
        var step = CheckoutStep();

        Assert.False(string.IsNullOrWhiteSpace(step.Shell), "the step must run something, not only declare a checkout");
        var (code, output) = RunShell(step.Shell, new() { ["BUILD_SOURCEVERSION"] = revision });

        Assert.True(code == 0, output);
        Assert.Contains(revision, output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCheckoutStep_Fails_WhenTheWorkspaceIsAnotherRevisionThanTheRunsOne()
    {
        CommitRepository();
        var other = new string('a', 40);

        var (code, output) = RunShell(CheckoutStep().Shell, new() { ["BUILD_SOURCEVERSION"] = other });

        Assert.NotEqual(0, code);
        Assert.Contains($"not the revision {other}", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCheckoutStep_Fails_WhenTheWorkspaceIsNotAGitCheckout()
    {
        var (code, output) = RunShell(CheckoutStep().Shell, new() { ["BUILD_SOURCEVERSION"] = new string('b', 40) });

        Assert.NotEqual(0, code);
        Assert.Contains("not a Git checkout", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCheckoutStep_Refuses_ARunThatNamesNoRevision()
    {
        CommitRepository();

        var (code, output) = RunShell(CheckoutStep().Shell, new() { ["BUILD_SOURCEVERSION"] = string.Empty });

        Assert.NotEqual(0, code);
        Assert.Contains("names no source revision", output, StringComparison.Ordinal);
    }

    // The replacement renders the environment of the release like the production deployment: the step declares the sample
    // (env_template), owns the vault the sample's secrets come from, and the default commands pass the rendered file to
    // Docker Compose. A project created by the setup wizard has no ENV_FILE: its secrets are in the vault `<project>.prod`.

    private static (PipelineStepDefinition Step, PipelineYamlDefinition Definition) ReplaceStep()
    {
        var definition = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(
            DeliveryTemplateCatalog.ReadResource(Template))!;
        var stage = Assert.Single(YamlParsingHelper.FlattenJobs(definition), item => item.Name == "FastRelease");
        return (Assert.Single(stage.Steps), definition);
    }

    [Fact]
    public void TheReplaceStep_RendersTheVersionedSample_AndOwnsTheProductionVault()
    {
        var (step, definition) = ReplaceStep();

        Assert.Equal("$(DEPLOY_ENV_SAMPLE)", step.EnvTemplate);
        Assert.Contains("AETHEUS_ENV_FILE_B64", step.Shell, StringComparison.Ordinal);
        Assert.Contains("--env-file", FastReleaseCommands(), StringComparison.Ordinal);
        Assert.Contains("${{ project.slug }}.prod", definition.Vaults);
    }

    [Fact]
    public void TheReplaceStep_HandsTheRenderedFileToTheCommands_AndErasesIt_WhateverTheOutcome()
    {
        var (step, _) = ReplaceStep();

        Assert.Contains("export DEPLOY_ENV_FILE", step.Shell, StringComparison.Ordinal);
        Assert.Contains("umask 077", step.Shell, StringComparison.Ordinal);
        Assert.Contains("trap 'rm -f \"$DEPLOY_ENV_FILE\"' EXIT", step.Shell, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultFastRelease_PassesTheRenderedEnvironmentFileToEveryComposeCall()
    {
        var revision = CommitRepository();

        var (code, output) = RunShell(WithRenderedEnvironmentFile(FastReleaseCommands()), FastEnvironment(revision));

        Assert.True(code == 0, output);
        var calls = DockerCalls().Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(call => call.Contains(" -f ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.Contains("--env-file", call, StringComparison.Ordinal));
        Assert.All(calls, call => Assert.Contains(".deploy-env.test", call, StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultFastRelease_NeverCopiesTheRenderedEnvironmentFile_IntoTheDeploymentDirectory()
    {
        var revision = CommitRepository();

        var (code, output) = RunShell(WithRenderedEnvironmentFile(FastReleaseCommands()), FastEnvironment(revision));

        Assert.True(code == 0, output);
        Assert.False(File.Exists(Path.Combine(_deployDirectory, ".deploy-env.test")), "the secrets file must stay out of the release copy");
        Assert.Empty(Directory.EnumerateFiles(_deployDirectory, ".deploy-env*", SearchOption.AllDirectories));
    }

    [Fact]
    public void TheDefaultFastRelease_UsesEnvFile_WhenTheLibraryNamesOne_InsteadOfTheRenderedFile()
    {
        var revision = CommitRepository();
        File.WriteAllText(Path.Combine(_repository, "server.env"), "A=1\n");
        var environment = FastEnvironment(revision);
        environment["ENV_FILE"] = "./server.env";

        var (code, output) = RunShell(WithRenderedEnvironmentFile(FastReleaseCommands()), environment);

        Assert.True(code == 0, output);
        var calls = DockerCalls();
        Assert.Contains("--env-file ./server.env", calls, StringComparison.Ordinal);
        Assert.DoesNotContain(".deploy-env.test", calls, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultFastRelease_Fails_WithNeitherARenderedFileNorEnvFile_AndStartsNothing()
    {
        var revision = CommitRepository();

        var (code, output) = RunShell(FastReleaseCommands(), FastEnvironment(revision));

        Assert.NotEqual(0, code);
        Assert.Contains("No environment file", output, StringComparison.Ordinal);
        Assert.DoesNotContain("up -d", DockerCalls(), StringComparison.Ordinal);
    }

    // R-37: the default fast release deploys the Compose file the wizard wrote as COMPOSE_FILE, wherever it is in the
    // repository, instead of looking for four names at the root and failing with "No Compose file".

    [Fact]
    public void TheDefaultFastRelease_StartsTheComposeFileNamedByComposeFile_InASubdirectory()
    {
        var revision = CommitRepository();

        var (code, output) = RunShell(WithRenderedEnvironmentFile(FastReleaseCommands()), FastEnvironment(revision));

        Assert.True(code == 0, output);
        var calls = DockerCalls();
        Assert.Contains($"-f {ComposePath} up -d --build", calls, StringComparison.Ordinal);
        Assert.Contains("-p atlas", calls, StringComparison.Ordinal);
        var release = Assert.Single(Directory.GetDirectories(Path.Combine(_deployDirectory, "releases")));
        Assert.True(File.Exists(Path.Combine(release, "deploy", "compose", "remote.compose.yml")),
            "the checked-out source must be copied to the release's build directory");
    }

    [Fact]
    public void TheDefaultFastRelease_StillFindsAComposeFileAtTheRoot_WhenNoneIsNamed()
    {
        var revision = CommitRepository("compose.yaml");

        var (code, output) = RunShell(WithRenderedEnvironmentFile(FastReleaseCommands()), FastEnvironment(revision, composeFile: null));

        Assert.True(code == 0, output);
        Assert.Contains("-f compose.yaml up -d --build", DockerCalls(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultFastRelease_Fails_WhenTheNamedComposeFileIsNotInTheRevision()
    {
        var revision = CommitRepository();

        var (code, output) = RunShell(FastReleaseCommands(), FastEnvironment(revision, composeFile: "deploy/compose/missing.yml"));

        Assert.NotEqual(0, code);
        Assert.Contains("missing.yml is not in the checked-out revision", output, StringComparison.Ordinal);
        Assert.DoesNotContain("up -d", DockerCalls(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultFastRelease_Fails_WithNoComposeFileAtAll_AndSaysHowToFixIt()
    {
        var revision = CommitRepository("README.md");

        var (code, output) = RunShell(FastReleaseCommands(), FastEnvironment(revision, composeFile: null));

        Assert.NotEqual(0, code);
        Assert.Contains("Set COMPOSE_FILE in this pipeline", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultFastRelease_Refuses_AWorkspaceThatIsNotTheRunsRevision_AndStartsNothing()
    {
        CommitRepository();
        var other = new string('c', 40);

        var (code, output) = RunShell(FastReleaseCommands(), FastEnvironment(other));

        Assert.NotEqual(0, code);
        Assert.Contains($"not the revision {other}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("up -d", DockerCalls(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultFastRelease_Fails_WhenTheWorkspaceHoldsNoRevision_InsteadOfDeployingWithAnUnknownCommit()
    {
        // Not a Git checkout: it used to deploy with "commit unknown".
        File.WriteAllText(Path.Combine(_repository, "compose.yaml"), "services: {}\n");

        var (code, output) = RunShell(FastReleaseCommands(), FastEnvironment(new string('d', 40), composeFile: null));

        Assert.NotEqual(0, code);
        Assert.Contains("no checked-out revision", output, StringComparison.Ordinal);
        Assert.DoesNotContain("up -d", DockerCalls(), StringComparison.Ordinal);
    }
}
