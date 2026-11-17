// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-7: the default Docker Compose deployments (<c>application-release-fast</c> and the production core
/// <c>application-deploy-prod-core</c>) build every release in a new, empty directory, so nothing a previous release left
/// can reach the build, while what must outlive a release stays outside it. These tests run the shell each template ships,
/// with a real <c>sh</c> and a recording stand-in for <c>docker</c>, and observe the file system and the calls. What they do
/// not prove is a Docker Compose start on a server (the stand-in answers every call): that is the real run of a deployment.
/// </summary>
public sealed class ReleaseBuildDirectoryTests : IDisposable
{
    private const string Fast = "fast";
    private const string Core = "core";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "release-build-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly string _binaries;
    private readonly string _deployDirectory;
    private int _releaseNumber;

    public ReleaseBuildDirectoryTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        _binaries = Path.Combine(_root, "bin");
        _deployDirectory = Path.Combine(_root, "deploy");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_binaries);
        var root = _root.Replace('\\', '/');
        // Records every call with the directory it runs in; `config` prints, for each relative path listed in <root>/binds,
        // the absolute bind mount Docker Compose would resolve inside the directory it runs in; `up` fails while
        // <root>/fail-up exists.
        var fakeDocker = Path.Combine(_binaries, "docker");
        File.WriteAllText(fakeDocker, $$"""
            #!/bin/sh
            printf 'cwd=%s args=%s\n' "$PWD" "$*" >> "{{root}}/docker-calls"
            case " $* " in
              *" config "*)
                if [ -f "{{root}}/binds" ]; then
                  while IFS= read -r relative; do
                    printf '    - type: bind\n      source: %s/%s\n      target: /x\n' "$PWD" "$relative"
                  done < "{{root}}/binds"
                fi
                printf '    - type: volume\n      source: named-volume\n      target: /y\n'
                ;;
              *" up "*)
                if [ -f "{{root}}/fail-up" ]; then exit 1; fi
                ;;
            esac
            exit 0
            """.Replace("\r\n", "\n"), new UTF8Encoding(false));
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

    private static string CommandsOf(string template) => template == Fast
        ? YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(
            DeliveryTemplateCatalog.ReadResource("generic/application-release-fast.yaml"))!.Variables["APPLICATION_RELEASE_FAST_COMMANDS"]
        : YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(
            DeliveryTemplateCatalog.ReadResource("generic/application-deploy-prod-core-v1.yaml"))!.Variables["APPLICATION_DEPLOY_PROD_COMMANDS"];

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

    private void Git(params string[] arguments)
    {
        var (code, output) = Run("git",
            ["-c", "user.name=test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", .. arguments], _workspace);
        Assert.True(code == 0, $"git {string.Join(' ', arguments)} failed: {output}");
    }

    /// <summary>
    /// Stages <paramref name="files"/> as the next release's source: committed in the workspace for the fast release, packed
    /// as the sealed source archive for the production core. Returns the lines to run before the template's commands.
    /// </summary>
    private string StageRelease(string template, IReadOnlyDictionary<string, string> files)
    {
        _releaseNumber++;
        var source = template == Fast ? _workspace : Path.Combine(_root, "sealed-" + _releaseNumber);
        Directory.CreateDirectory(source);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source).Where(path => Path.GetFileName(path) is not (".git" or ".deploy-env.test")))
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
        foreach (var (relative, content) in files)
        {
            var file = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }
        var prefix = new StringBuilder("printf 'DB_PASSWORD=secret\\n' > .deploy-env.test\nDEPLOY_ENV_FILE=\"$PWD/.deploy-env.test\"\nexport DEPLOY_ENV_FILE\n");
        if (template == Fast)
        {
            if (!Directory.Exists(Path.Combine(_workspace, ".git"))) Git("init", "-q");
            Git("add", "-A");
            Git("commit", "-q", "--allow-empty", "-m", $"release {_releaseNumber}");
        }
        else
        {
            prefix.Append("mkdir -p .pipeline-artifacts\n")
                .Append($"tar -czf .pipeline-artifacts/source.tar.gz -C \"{source.Replace('\\', '/')}\" .\n")
                .Append($"printf 'sealed-{_releaseNumber}\\n' > .pipeline-artifacts/source-commit\n");
        }
        return prefix.ToString();
    }

    private string Head() => Run("git", ["rev-parse", "HEAD"], _workspace).Output.Trim();

    private Dictionary<string, string> Environment(string template, string? composeFile = "compose.yaml")
    {
        var environment = new Dictionary<string, string>
        {
            ["COMPOSE_PROJECT"] = "atlas",
            ["DEPLOY_DIR"] = "../deploy",
            ["APPLICATION_CANDIDATE_VERSION"] = "1.2." + _releaseNumber
        };
        if (composeFile is not null) environment["COMPOSE_FILE"] = composeFile;
        if (template == Fast) environment["BUILD_SOURCEVERSION"] = Head();
        var shell = PosixShell();
        environment["PATH"] = _binaries + Path.PathSeparator + Path.GetDirectoryName(shell) + Path.PathSeparator
            + System.Environment.GetEnvironmentVariable("PATH");
        return environment;
    }

    /// <summary>Runs one release of <paramref name="template"/> over <paramref name="files"/>, then <paramref name="probe"/> in the same shell.</summary>
    private (int ExitCode, string Output) Release(
        string template, IReadOnlyDictionary<string, string> files, string probe = "", string? composeFile = "compose.yaml")
    {
        var prefix = StageRelease(template, files);
        var environment = Environment(template, composeFile);
        var script = prefix + CommandsOf(template) + "\n" + probe + "\n";
        var scriptFile = Path.Combine(_root, "script-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(scriptFile, script, new UTF8Encoding(false));
        return Run(PosixShell(), ["-eu", scriptFile], _workspace, environment);
    }

    private static Dictionary<string, string> Source(params string[] extraFiles)
    {
        var files = new Dictionary<string, string> { ["compose.yaml"] = "services: {}\n" };
        foreach (var file in extraFiles) files[file] = file;
        return files;
    }

    private string Pointer(string name)
    {
        var file = Path.Combine(_deployDirectory, ".release-" + name);
        return File.Exists(file) ? File.ReadAllText(file).Trim() : string.Empty;
    }

    private string ReleaseDirectory(string name) => Path.Combine(_deployDirectory, "releases", name);

    private List<string> DockerCalls() =>
        File.Exists(Path.Combine(_root, "docker-calls"))
            ? [.. File.ReadAllLines(Path.Combine(_root, "docker-calls")).Where(line => line.Length > 0)]
            : [];

    private static string CurrentDirectoryOf(string call) =>
        call[(call.IndexOf("cwd=", StringComparison.Ordinal) + 4)..call.IndexOf(" args=", StringComparison.Ordinal)];

    [Theory]
    [InlineData(Fast)]
    [InlineData(Core)]
    public void AFileOfAPreviousRelease_IsNotInTheNextReleasesBuildDirectory(string template)
    {
        var (first, firstOutput) = Release(template, Source("src/Data/DbInitializer.cs", "src/Program.cs"));
        Assert.True(first == 0, firstOutput);
        var firstRelease = Pointer("current");

        var (second, secondOutput) = Release(template, Source("src/Infrastructure/DbInitializer.cs", "src/Program.cs"));

        Assert.True(second == 0, secondOutput);
        var secondRelease = Pointer("current");
        Assert.NotEqual(firstRelease, secondRelease);
        Assert.True(File.Exists(Path.Combine(ReleaseDirectory(secondRelease), "src", "Infrastructure", "DbInitializer.cs")));
        Assert.False(File.Exists(Path.Combine(ReleaseDirectory(secondRelease), "src", "Data", "DbInitializer.cs")),
            "the build directory of a release holds only the files of that release");
        // The previous release keeps its own sources, for a manual rollback.
        Assert.True(File.Exists(Path.Combine(ReleaseDirectory(firstRelease), "src", "Data", "DbInitializer.cs")));
        Assert.Equal(firstRelease, Pointer("previous"));
    }

    [Theory]
    [InlineData(Fast)]
    [InlineData(Core)]
    public void TheComposeProjectNameStaysTheSame_WhileTheBuildDirectoryChanges(string template)
    {
        Assert.Equal(0, Release(template, Source("a.txt")).ExitCode);
        Assert.Equal(0, Release(template, Source("b.txt")).ExitCode);

        var calls = DockerCalls().Where(call => call.Contains(" args=compose ", StringComparison.Ordinal) && call.Contains(" -f ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.Contains(" -p atlas ", call, StringComparison.Ordinal));
        var upDirectories = calls.Where(call => call.Contains(" up ", StringComparison.Ordinal)).Select(CurrentDirectoryOf).ToList();
        Assert.Equal(2, upDirectories.Count);
        Assert.NotEqual(upDirectories[0], upDirectories[1]);
        Assert.All(upDirectories, directory => Assert.Contains("/releases/", directory, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Fast)]
    [InlineData(Core)]
    public void OnlyTheCurrentAndThePreviousReleaseAreKept(string template)
    {
        Assert.Equal(0, Release(template, Source("1.txt")).ExitCode);
        var first = Pointer("current");
        Assert.Equal(0, Release(template, Source("2.txt")).ExitCode);
        var second = Pointer("current");
        Assert.Equal(0, Release(template, Source("3.txt")).ExitCode);
        var third = Pointer("current");

        Assert.Equal(second, Pointer("previous"));
        var kept = Directory.GetDirectories(Path.Combine(_deployDirectory, "releases")).Select(path => Path.GetFileName(path)!).Order().ToList();
        Assert.Equal(new[] { second, third }.Order().ToList(), kept);
        Assert.DoesNotContain(first, kept);
    }

    [Theory]
    [InlineData(Fast)]
    [InlineData(Core)]
    public void AFailedRelease_LeavesTheLastGoodOneAsCurrent_AndItsLeftoverIsRemovedByTheNextRelease(string template)
    {
        Assert.Equal(0, Release(template, Source("good.txt")).ExitCode);
        var good = Pointer("current");
        File.WriteAllText(Path.Combine(_root, "fail-up"), "x");

        var (code, output) = Release(template, Source("bad.txt"));

        Assert.NotEqual(0, code);
        Assert.Contains("did not", output, StringComparison.Ordinal);
        Assert.Equal(good, Pointer("current"));
        Assert.True(File.Exists(Path.Combine(ReleaseDirectory(good), "good.txt")), "the release that runs keeps its sources");
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(_deployDirectory, "releases")).Length);

        File.Delete(Path.Combine(_root, "fail-up"));
        Assert.Equal(0, Release(template, Source("fixed.txt")).ExitCode);
        Assert.Equal(good, Pointer("previous"));
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(_deployDirectory, "releases")).Length);
    }

    [Theory]
    [InlineData(Fast)]
    [InlineData(Core)]
    public void BindMountedRuntimeData_StaysOutsideTheBuildDirectory_AndSurvivesEveryRelease(string template)
    {
        File.WriteAllText(Path.Combine(_root, "binds"), "uploads\ndata/db\nnginx.conf\n");
        // What the earlier releases, extracted in DEPLOY_DIR itself, left there as runtime data.
        Directory.CreateDirectory(Path.Combine(_deployDirectory, "uploads"));
        File.WriteAllText(Path.Combine(_deployDirectory, "uploads", "legacy.txt"), "legacy");
        // And the manifest the former cleanup kept: it has no use any more.
        File.WriteAllText(Path.Combine(_deployDirectory, ".release-files"), "old\n");

        var probe = "echo \"PROBE-LEGACY=$(cat \"$BUILD_DIR/uploads/legacy.txt\")\"\n"
            + "echo \"PROBE-CONF=$(cat \"$BUILD_DIR/nginx.conf\")\"";
        var (first, firstOutput) = Release(template, Source("nginx.conf"), probe);

        Assert.True(first == 0, firstOutput);
        Assert.Contains("PROBE-LEGACY=legacy", firstOutput, StringComparison.Ordinal);
        Assert.Contains("PROBE-CONF=nginx.conf", firstOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_deployDirectory, ".release-files")), "the manifest cleanup is replaced by the build directories");
        Assert.True(File.Exists(Path.Combine(_deployDirectory, "shared", "uploads", "legacy.txt")), "the earlier runtime data is moved, not copied or lost");
        Assert.False(Directory.Exists(Path.Combine(_deployDirectory, "uploads")));
        Assert.True(Directory.Exists(Path.Combine(_deployDirectory, "shared", "data", "db")));
        Assert.False(File.Exists(Path.Combine(_deployDirectory, "shared", "nginx.conf")), "a bind-mounted file the release holds is source");

        // The application writes data while it runs; two more releases follow, which prune the first one.
        File.WriteAllText(Path.Combine(_deployDirectory, "shared", "uploads", "runtime.txt"), "written while running");
        File.WriteAllText(Path.Combine(_deployDirectory, "shared", "data", "db", "pg.dat"), "database");
        var again = "echo \"PROBE-RUNTIME=$(cat \"$BUILD_DIR/uploads/runtime.txt\")\"\necho \"PROBE-DB=$(cat \"$BUILD_DIR/data/db/pg.dat\")\"";
        var (second, secondOutput) = Release(template, Source("nginx.conf", "extra.txt"), again);
        Assert.True(second == 0, secondOutput);
        Assert.Contains("PROBE-RUNTIME=written while running", secondOutput, StringComparison.Ordinal);
        Assert.Contains("PROBE-DB=database", secondOutput, StringComparison.Ordinal);
        var (third, thirdOutput) = Release(template, Source("nginx.conf", "extra2.txt"), again);
        Assert.True(third == 0, thirdOutput);
        Assert.Contains("PROBE-DB=database", thirdOutput, StringComparison.Ordinal);

        Assert.Equal("written while running", File.ReadAllText(Path.Combine(_deployDirectory, "shared", "uploads", "runtime.txt")));
        Assert.Equal("database", File.ReadAllText(Path.Combine(_deployDirectory, "shared", "data", "db", "pg.dat")));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(_deployDirectory, "shared", "uploads", "legacy.txt")));
    }

    [Theory]
    [InlineData(Fast)]
    [InlineData(Core)]
    public void ABindMountOnANameTheDeploymentDirectoryReserves_IsRefused_BeforeAnythingStarts(string template)
    {
        File.WriteAllText(Path.Combine(_root, "binds"), "shared/data\n");

        var (code, output) = Release(template, Source());

        Assert.NotEqual(0, code);
        Assert.Contains("reserves", output, StringComparison.Ordinal);
        Assert.DoesNotContain(" up ", string.Join('\n', DockerCalls()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Fast)]
    [InlineData(Core)]
    public void TheRenderedEnvironmentFile_IsNeverCopiedIntoAReleaseDirectory(string template)
    {
        Assert.Equal(0, Release(template, Source("src/app.cs")).ExitCode);

        Assert.Empty(Directory.EnumerateFiles(_deployDirectory, ".deploy-env*", SearchOption.AllDirectories));
    }
}
