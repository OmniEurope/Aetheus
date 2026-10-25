// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Small agent helpers that carry real decisions but had no test of their own: the deployment-only
/// build classifier, the options validator, the task-target parsers and the environment normalizer.
/// Each is pure or filesystem-local, so the assertions are on the decision, not on a mock.
/// </summary>
public sealed class AgentPureHelperSweepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- DockerBuildCommandClassifier ----------

    [Theory]
    [InlineData("docker build .")]
    [InlineData("docker buildx build --push .")]
    [InlineData("/usr/bin/docker build .")]
    [InlineData("sudo docker build .")]
    [InlineData("env FOO=bar docker build .")]
    [InlineData("nohup exec docker build .")]
    [InlineData("docker compose -f x.yml build")]
    [InlineData("docker compose up --build")]
    [InlineData("echo hi; docker build .")]
    [InlineData("echo hi && docker build .")]
    [InlineData("echo hi\ndocker build .")]
    [InlineData("$COMPOSE up --build")]
    [InlineData("${COMPOSE} -f x.yml build")]
    [InlineData("bash -c \"docker build .\"")]
    [InlineData("sh -c 'docker build .'")]
    public void Classify_RecognizesABuildInEveryWrapperShape(string command)
    {
        Assert.True(DockerBuildCommandClassifier.Classify(command, deploymentOnly: false).IsBuild);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("docker ps")]
    [InlineData("docker compose up -d")]
    [InlineData("dotnet build")]
    [InlineData("mydocker build .")]
    public void Classify_LeavesNonBuildCommandsAlone(string command)
    {
        Assert.False(DockerBuildCommandClassifier.Classify(command, deploymentOnly: false).IsBuild);
    }

    [Fact]
    public void Classify_OnADeploymentOnlyAgentAlsoCatchesTheContextFlaggedForm()
    {
        const string command = "docker --context remote build .";

        Assert.False(DockerBuildCommandClassifier.Classify(command, deploymentOnly: false).IsBuild);
        Assert.True(DockerBuildCommandClassifier.Classify(command, deploymentOnly: true).IsBuild);
    }

    [Fact]
    public void Classify_FailsClosedOnDeploymentOnlyAgentsWhenTheWrapperNestingIsAbsurd()
    {
        var nested = "docker build .";
        for (var depth = 0; depth < 20; depth++)
            nested = $"bash -c \"{nested.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

        Assert.True(DockerBuildCommandClassifier.Classify(nested, deploymentOnly: true).IsBuild);
        Assert.False(DockerBuildCommandClassifier.Classify(nested, deploymentOnly: false).IsBuild);
    }

    [Fact]
    public void Classify_ReportsThatItDidNotTimeOutOnOrdinaryInput()
    {
        Assert.False(DockerBuildCommandClassifier.Classify("docker build .", false).TimedOut);
    }

    // ---------- AetheusAgentOptionsValidator ----------

    [Fact]
    public void OptionsValidator_AcceptsTheShippedDefaults()
    {
        Assert.True(new AetheusAgentOptionsValidator().Validate(null, new AetheusAgentOptions()).Succeeded);
    }

    [Theory]
    [InlineData(nameof(AetheusAgentOptions.PollingIntervalSeconds), 0)]
    [InlineData(nameof(AetheusAgentOptions.HeartbeatIntervalSeconds), 3601)]
    [InlineData(nameof(AetheusAgentOptions.HeartbeatCollectionTimeoutSeconds), 0)]
    [InlineData(nameof(AetheusAgentOptions.MaxConcurrentTasks), 11)]
    [InlineData(nameof(AetheusAgentOptions.LogRetentionDays), 0)]
    [InlineData(nameof(AetheusAgentOptions.DeployReleasesToKeep), 101)]
    [InlineData(nameof(AetheusAgentOptions.DeployMinFreeSpaceMiB), 0)]
    [InlineData(nameof(AetheusAgentOptions.DeployUnpackRatioEstimate), 101)]
    public void OptionsValidator_RejectsAnOutOfRangeSettingAndNamesIt(string property, int value)
    {
        var options = new AetheusAgentOptions();
        typeof(AetheusAgentOptions).GetProperty(property)!.SetValue(options, value);

        var result = new AetheusAgentOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains(property, StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_RejectsATimeoutWindowThatIsInverted()
    {
        var options = new AetheusAgentOptions { MinTimeoutSeconds = 600, MaxTimeoutSeconds = 300 };

        var result = new AetheusAgentOptionsValidator().Validate(null, options);

        Assert.Contains(
            result.Failures!,
            failure => failure.Contains("greater than or equal", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsValidator_RejectsEmptyDirectories()
    {
        var options = new AetheusAgentOptions { WorkDirectory = " ", PluginDirectory = "" };

        var result = new AetheusAgentOptionsValidator().Validate(null, options);

        Assert.Contains(result.Failures!, failure => failure.Contains("WorkDirectory", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("PluginDirectory", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef", true)]
    [InlineData("deadbeef", false)]
    [InlineData("zzzz456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    public void OptionsValidator_AcceptsOnlyASha256Thumbprint(string thumbprint, bool expected)
    {
        var options = new AetheusAgentOptions { PinnedServerCertThumbprint = thumbprint };

        Assert.Equal(expected, new AetheusAgentOptionsValidator().Validate(null, options).Succeeded);
    }

    // ---------- PipelineTargetPatterns ----------

    [Fact]
    public void ParseOptional_ReadsAJsonArrayAndFallsBackToTheRawTarget()
    {
        Assert.Equal(["a.json", "b.json"], PipelineTargetPatterns.ParseOptional("[\"a.json\",\"b.json\"]"));
        Assert.Equal(["appsettings.json"], PipelineTargetPatterns.ParseOptional("appsettings.json"));
        Assert.Null(PipelineTargetPatterns.ParseOptional("   "));
    }

    // ---------- StorageSizeFormatting ----------

    [Fact]
    public void FormatBytes_SwitchesToGibibytesAtTheBoundary()
    {
        Assert.Equal("512 Mo", StorageSizeFormatting.FormatBytes(512L * 1024 * 1024));
        Assert.Equal("1 Go", StorageSizeFormatting.FormatBytes(StorageSizeFormatting.GiB(1)));
        Assert.Equal("1,5 Go".Replace(',', '.'), StorageSizeFormatting.FormatBytes(
            StorageSizeFormatting.GiB(1) + 512L * 1024 * 1024).Replace(',', '.'));
    }

    // ---------- OperationPlatformGuard ----------

    [Fact]
    public async Task RequireLinuxAsync_LetsLinuxThroughAndStopsEveryOtherPlatform()
    {
        var messages = new List<string>();
        var result = await OperationPlatformGuard.RequireLinuxAsync(
            (message, _) => { messages.Add(message); return Task.CompletedTask; },
            "Linux only.");

        if (OperatingSystem.IsLinux())
        {
            Assert.Null(result);
            Assert.Empty(messages);
        }
        else
        {
            Assert.Equal(-1, result!.ExitCode);
            Assert.Equal(["Linux only."], messages);
        }
    }

    // ---------- OperationExecutorFailure ----------

    [Fact]
    public async Task ValidateTargetAsync_PassesAValidTargetThroughWithoutSideEffects()
    {
        var warned = false;
        var messages = new List<string>();

        var result = await OperationExecutorFailure.ValidateTargetAsync(
            OperationKind.ServiceRestart,
            "nginx",
            (message, _) => { messages.Add(message); return Task.CompletedTask; },
            () => warned = true,
            "Invalid target.");

        Assert.Null(result);
        Assert.False(warned);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task ValidateTargetAsync_WarnsAndFailsOnARejectedTarget()
    {
        var warned = false;
        var messages = new List<string>();

        var result = await OperationExecutorFailure.ValidateTargetAsync(
            OperationKind.ServiceRestart,
            "nginx; rm -rf /",
            (message, _) => { messages.Add(message); return Task.CompletedTask; },
            () => warned = true,
            "Invalid target.");

        Assert.Equal(-1, result!.ExitCode);
        Assert.False(result.TimedOut);
        Assert.True(warned);
        Assert.Equal(["Invalid target."], messages);
    }

    // ---------- PendingTaskEnvironmentNormalizer ----------

    [Fact]
    public void RehomeMirrorCloneUrls_SwapsTheAuthorityOfEveryMirrorUrlKey()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["REPOSITORY_URL"] = "https://control-plane:5001/git/42/web.git",
            ["BUILD_REPOSITORY_URI"] = "https://control-plane:5001/git/42/web.git",
            ["UNRELATED_URL"] = "https://control-plane:5001/git/42/web.git"
        };

        PendingTaskEnvironmentNormalizer.RehomeMirrorCloneUrls(
            environment, "https://agent-visible:8443", NullLogger.Instance);

        Assert.StartsWith("https://agent-visible:8443/", environment["REPOSITORY_URL"], StringComparison.Ordinal);
        Assert.EndsWith("/git/42/web.git", environment["BUILD_REPOSITORY_URI"], StringComparison.Ordinal);
        Assert.Equal(
            "https://control-plane:5001/git/42/web.git",
            environment["UNRELATED_URL"]);
    }

    [Fact]
    public void RehomeMirrorCloneUrls_LeavesAnAlreadyLocalOrNonMirrorUrlUntouched()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["REPOSITORY_URL"] = "https://github.com/acme/web.git"
        };

        PendingTaskEnvironmentNormalizer.RehomeMirrorCloneUrls(
            environment, "https://agent-visible:8443", NullLogger.Instance);

        Assert.Equal("https://github.com/acme/web.git", environment["REPOSITORY_URL"]);
    }

    [Fact]
    public void RehomeMirrorCloneUrls_IsANoOpWhenNeitherKeyIsPresent()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["OTHER"] = "x" };

        PendingTaskEnvironmentNormalizer.RehomeMirrorCloneUrls(
            environment, "https://agent-visible:8443", NullLogger.Instance);

        Assert.Equal(["OTHER"], environment.Keys);
    }

    [Fact]
    public void NormalizeContainerOperationWorkspace_PointsAContainerRunAtItsPerRunWorkspace()
    {
        var task = new PendingTaskDto
        {
            Id = 1,
            PipelineRunId = 42,
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AETHEUS_WORKSPACE_MODE"] = "Container"
            }
        };

        PendingTaskEnvironmentNormalizer.NormalizeContainerOperationWorkspace(task, "/var/lib/aetheus");

        Assert.Equal(
            Path.Combine("/var/lib/aetheus", "cw", "42"),
            task.EnvironmentVariables["AETHEUS_WORKING_DIR"]);
    }

    [Theory]
    [InlineData(null, "container")]
    [InlineData(42, "host")]
    [InlineData(42, null)]
    public void NormalizeContainerOperationWorkspace_LeavesEveryOtherTaskAlone(int? runId, string? mode)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mode is not null) environment["AETHEUS_WORKSPACE_MODE"] = mode;
        var task = new PendingTaskDto { Id = 1, PipelineRunId = runId, EnvironmentVariables = environment };

        PendingTaskEnvironmentNormalizer.NormalizeContainerOperationWorkspace(task, "/var/lib/aetheus");

        Assert.DoesNotContain("AETHEUS_WORKING_DIR", task.EnvironmentVariables.Keys);
    }

    // ---------- BackupFilesystemMetadata ----------

    [Fact]
    public void ApplyFileMetadata_RestoresTheRecordedModificationTime()
    {
        using var workspace = new TempWorkspace();
        var file = workspace.WriteFile("payload.txt", "content");
        var recorded = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        BackupFilesystemMetadata.ApplyFileMetadata(
            file,
            new BackupBundleFile { EntryName = "payload.txt", LastWriteTimeUtcTicks = recorded.Ticks });

        Assert.Equal(recorded, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void ApplyFileMetadata_IsANoOpWhenTheManifestRecordedNoTimestamp()
    {
        using var workspace = new TempWorkspace();
        var file = workspace.WriteFile("payload.txt", "content");
        var before = File.GetLastWriteTimeUtc(file);

        BackupFilesystemMetadata.ApplyFileMetadata(file, new BackupBundleFile { EntryName = "payload.txt" });

        Assert.Equal(before, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void ApplyDirectoryMetadata_RestoresTheRecordedModificationTime()
    {
        using var workspace = new TempWorkspace();
        var directory = workspace.CreateDirectory("nested");
        var recorded = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);

        BackupFilesystemMetadata.ApplyDirectoryMetadata(
            directory,
            new BackupBundleDirectory { EntryRoot = "nested", LastWriteTimeUtcTicks = recorded.Ticks });

        Assert.Equal(recorded, Directory.GetLastWriteTimeUtc(directory));
    }

    [Fact]
    public void Copy_CarriesTheModificationTimeFromSourceToTargetForFilesAndDirectories()
    {
        using var workspace = new TempWorkspace();
        var sourceFile = workspace.WriteFile("source.txt", "content");
        var targetFile = workspace.WriteFile("target.txt", "other");
        File.SetLastWriteTimeUtc(sourceFile, new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc));
        var sourceDirectory = workspace.CreateDirectory("from");
        var targetDirectory = workspace.CreateDirectory("to");
        Directory.SetLastWriteTimeUtc(sourceDirectory, new DateTime(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc));

        BackupFilesystemMetadata.Copy(sourceFile, targetFile);
        BackupFilesystemMetadata.Copy(sourceDirectory, targetDirectory);

        Assert.Equal(File.GetLastWriteTimeUtc(sourceFile), File.GetLastWriteTimeUtc(targetFile));
        Assert.Equal(
            Directory.GetLastWriteTimeUtc(sourceDirectory),
            Directory.GetLastWriteTimeUtc(targetDirectory));
    }

    [Fact]
    public void CreatePrivateDirectory_CreatesTheDirectoryAndToleratesAnExistingOne()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "private");

        BackupFilesystemMetadata.CreatePrivateDirectory(path);
        BackupFilesystemMetadata.CreatePrivateDirectory(path);

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void GetUnixMode_ReportsNothingOnWindowsAndAModeElsewhere()
    {
        using var workspace = new TempWorkspace();
        var file = workspace.WriteFile("payload.txt", "content");

        var mode = BackupFilesystemMetadata.GetUnixMode(file);

        if (OperatingSystem.IsWindows()) Assert.Null(mode);
        else Assert.NotNull(mode);
    }

    // ---------- ScannerFileSecurity ----------

    [Fact]
    public void TrySetOwnerOnly_NeverThrowsEvenForAPathThatDoesNotExist()
    {
        ScannerFileSecurity.TrySetOwnerOnly(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"));
    }

    // ---------- PipelineVariableSubstituter ----------

    [Fact]
    public async Task SubstituteAsync_ReplacesEveryTokenAndReportsTheCount()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteFile("appsettings.json", "{\"url\":\"#{API_URL}#\",\"env\":\"#{STAGE}#\"}");
        var messages = new List<string>();

        var result = await PipelineVariableSubstituter.SubstituteAsync(
            "appsettings.json",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AETHEUS_WORKING_DIR"] = workspace.Root,
                ["API_URL"] = "https://api.test",
                ["STAGE"] = "staging"
            },
            (message, _) => { messages.Add(message); return Task.CompletedTask; },
            Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "{\"url\":\"https://api.test\",\"env\":\"staging\"}",
            await File.ReadAllTextAsync(Path.Combine(workspace.Root, "appsettings.json"), Ct));
        Assert.Contains(messages, message => message.Contains("2 token(s) in 1 file(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubstituteAsync_FailsWhenNoTargetWasGiven()
    {
        var messages = new List<string>();

        var result = await PipelineVariableSubstituter.SubstituteAsync(
            "   ",
            new Dictionary<string, string>(StringComparer.Ordinal),
            (message, _) => { messages.Add(message); return Task.CompletedTask; },
            Ct);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(messages, message => message.Contains("no target files were specified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubstituteAsync_FailsWhenTheTargetMatchedNothing()
    {
        using var workspace = new TempWorkspace();
        var messages = new List<string>();

        var result = await PipelineVariableSubstituter.SubstituteAsync(
            "missing.json",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AETHEUS_WORKING_DIR"] = workspace.Root
            },
            (message, _) => { messages.Add(message); return Task.CompletedTask; },
            Ct);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(messages, message => message.Contains("no target files matched", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubstituteAsync_LeavesAMatchedFileWithoutTokensByteIdentical()
    {
        using var workspace = new TempWorkspace();
        var file = workspace.WriteFile("appsettings.json", "{\"url\":\"https://fixed\"}");
        var messages = new List<string>();

        var result = await PipelineVariableSubstituter.SubstituteAsync(
            "[\"appsettings.json\"]",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AETHEUS_WORKING_DIR"] = workspace.Root,
                ["API_URL"] = "https://api.test"
            },
            (message, _) => { messages.Add(message); return Task.CompletedTask; },
            Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("{\"url\":\"https://fixed\"}", await File.ReadAllTextAsync(file, Ct));
        Assert.Contains(messages, message => message.Contains("0 token(s) in 0 file(s)", StringComparison.Ordinal));
    }

    // ---------- WindowsAgentUpdaterScript ----------

    [Fact]
    public void WindowsUpdaterScript_IsWrittenIntoTheStagingRootAndTakesItsInputsAsParameters()
    {
        using var workspace = new TempWorkspace();

        var scriptPath = WindowsAgentUpdaterScript.Write(workspace.Root);

        Assert.Equal(Path.Combine(workspace.Root, "apply-update.ps1"), scriptPath);
        var script = File.ReadAllText(scriptPath);
        Assert.Contains("[int]$AgentPid", script, StringComparison.Ordinal);
        Assert.Contains("[string]$Src", script, StringComparison.Ordinal);
        Assert.Contains("[string]$Dest", script, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Root, script, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsUpdaterScript_KeepsTheOperatorSettingsAndTheRollbackSnapshot()
    {
        using var workspace = new TempWorkspace();

        var script = File.ReadAllText(WindowsAgentUpdaterScript.Write(workspace.Root));

        Assert.Contains("appsettings.json", script, StringComparison.Ordinal);
        Assert.Contains(".agent-rollback", script, StringComparison.Ordinal);
        Assert.Contains(".agent-update-pending", script, StringComparison.Ordinal);
        Assert.Contains("ReparsePoint", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsUpdaterScript_OverwritesAPreviousGeneration()
    {
        using var workspace = new TempWorkspace();
        var scriptPath = Path.Combine(workspace.Root, "apply-update.ps1");
        File.WriteAllText(scriptPath, "stale");

        WindowsAgentUpdaterScript.Write(workspace.Root);

        Assert.DoesNotContain("stale", File.ReadAllText(scriptPath), StringComparison.Ordinal);
    }

    /// <summary>A throwaway directory removed on dispose, so no test leaves state behind.</summary>
    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } =
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"aetheus-{Guid.NewGuid():N}")).FullName;

        public string WriteFile(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public string CreateDirectory(string relativePath) =>
            Directory.CreateDirectory(Path.Combine(Root, relativePath)).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* a scanner may still hold a handle; the temp dir is disposable */ }
        }
    }
}
