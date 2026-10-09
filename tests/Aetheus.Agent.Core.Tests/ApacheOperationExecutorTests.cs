// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

public class ApacheOperationExecutorTests
{
    private static ApacheOperationExecutor Build() =>
        new(Options.Create(new AetheusAgentOptions()), NullLogger<ApacheOperationExecutor>.Instance);

    private static Task NoOutput(string _, TaskLogLevel __) => Task.CompletedTask;

    [Theory]
    [InlineData(OperationKind.ApacheGetConfig, true)]
    [InlineData(OperationKind.ApacheGetHtaccess, true)]
    [InlineData(OperationKind.ApacheSaveHtaccess, true)]
    [InlineData(OperationKind.ApacheReload, true)]
    [InlineData(OperationKind.ApacheApplyConfigSet, true)]
    [InlineData(OperationKind.CertbotObtain, false)]
    [InlineData(OperationKind.PortsentryUnblock, false)]
    public void CanHandle_IncludesNewReadWriteKinds(OperationKind kind, bool expected)
        => Assert.Equal(expected, Build().CanHandle(kind));

    // Note: production dispatch (PollingService) always calls the env-aware overload, which is where the
    // typed read/write ops are routed - the tests use it too (an empty dict for the no-payload reads).
    private static readonly IReadOnlyDictionary<string, string> NoEnv = new Dictionary<string, string>();

    [Fact]
    public async Task GetHtaccess_InvalidDocumentRoot_ReturnsFailureWithoutTouchingFs()
    {
        var messages = new List<string>();
        var result = await Build().ExecuteAsync(
            OperationKind.ApacheGetHtaccess, "../../etc", NoEnv, 10,
            (m, _) => { messages.Add(m); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(messages, m => m.Contains("Invalid document root", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetHtaccess_AbsentFile_ReturnsEmptySuccess()
    {
        // A valid unix-absolute path that does not exist: the read is an honest empty success, not a failure.
        var result = await Build().ExecuteAsync(
            OperationKind.ApacheGetHtaccess, "/nonexistent/aetheus-test-dir", NoEnv, 10,
            NoOutput, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task SaveHtaccess_MissingEnvPayload_ReturnsFailure()
    {
        var messages = new List<string>();
        var result = await Build().ExecuteAsync(
            OperationKind.ApacheSaveHtaccess, "/var/www/html",
            new Dictionary<string, string>(), 10,
            (m, _) => { messages.Add(m); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(messages, m => m.Contains("AETHEUS_APACHE_HTACCESS_B64", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveHtaccess_InvalidBase64_ReturnsFailure()
    {
        var messages = new List<string>();
        var result = await Build().ExecuteAsync(
            OperationKind.ApacheSaveHtaccess, "/var/www/html",
            new Dictionary<string, string> { ["AETHEUS_APACHE_HTACCESS_B64"] = "not-base64!!!" }, 10,
            (m, _) => { messages.Add(m); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(messages, m => m.Contains("base64", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SaveThenGetHtaccess_RoundTrips()
    {
        byte[]? storedContent = null;
        string? storedPath = null;
        var executor = new ApacheOperationExecutor(
            Options.Create(new AetheusAgentOptions()),
            NullLogger<ApacheOperationExecutor>.Instance)
        {
            HtaccessFileExists = path => path == storedPath && storedContent is not null,
            WriteHtaccessAsync = (path, bytes, _) =>
            {
                storedPath = path;
                storedContent = bytes.ToArray();
                return Task.CompletedTask;
            },
            ReadHtaccessAsync = (_, _) => Task.FromResult(Encoding.UTF8.GetString(storedContent!))
        };
        const string documentRoot = "/var/www/html";
        const string content = "RewriteEngine On\nRewriteRule ^old$ /new [R=301]\n";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));

        var save = await executor.ExecuteAsync(
            OperationKind.ApacheSaveHtaccess, documentRoot,
            new Dictionary<string, string> { ["AETHEUS_APACHE_HTACCESS_B64"] = b64 }, 10,
            NoOutput, TestContext.Current.CancellationToken);

        Assert.Equal(0, save.ExitCode);
        Assert.EndsWith(".htaccess", storedPath, StringComparison.Ordinal);
        Assert.Equal(content, Encoding.UTF8.GetString(storedContent!));

        var read = new List<string>();
        var get = await executor.ExecuteAsync(
            OperationKind.ApacheGetHtaccess, documentRoot, NoEnv, 10,
            (message, _) =>
            {
                read.Add(message);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, get.ExitCode);
        Assert.Contains(read, message => message.Contains("RewriteEngine On", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyConfigSet_ValidManifest_TestsThenReloadsAndEnablesEveryFile()
    {
        using var layout = new ApacheTestLayout();
        var operations = new List<OperationKind>();
        var executor = layout.Build((kind, _, _, _) =>
        {
            operations.Add(kind);
            return Task.FromResult(new ExecutorResult(0, false));
        });
        var env = BuildConfigSet(("app.conf", "ServerName app.example.test\n"),
            ("api.conf", "ServerName api.example.test\n"));

        var result = await executor.ExecuteAsync(
            OperationKind.ApacheApplyConfigSet, "config-set", env, 10, NoOutput,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([OperationKind.ApacheTestConfig, OperationKind.ApacheReload], operations);
        Assert.Equal("ServerName app.example.test\n", await File.ReadAllTextAsync(
            Path.Combine(layout.Available, "app.conf"), TestContext.Current.CancellationToken));
        Assert.Equal("ServerName api.example.test\n", await File.ReadAllTextAsync(
            Path.Combine(layout.Available, "api.conf"), TestContext.Current.CancellationToken));
        Assert.Equal(Path.Combine(layout.Available, "app.conf"), await File.ReadAllTextAsync(
            Path.Combine(layout.Enabled, "app.conf"), TestContext.Current.CancellationToken));
        Assert.Equal(Path.Combine(layout.Available, "api.conf"), await File.ReadAllTextAsync(
            Path.Combine(layout.Enabled, "api.conf"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplyConfigSet_ConfigTestFails_RestoresPreviousFileAndReloadsIt()
    {
        using var layout = new ApacheTestLayout();
        var available = Path.Combine(layout.Available, "app.conf");
        var enabled = Path.Combine(layout.Enabled, "app.conf");
        await File.WriteAllTextAsync(available, "old-config\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(enabled, available, TestContext.Current.CancellationToken);
        var operations = new List<OperationKind>();
        var configTestCount = 0;
        var executor = layout.Build((kind, _, _, _) =>
        {
            operations.Add(kind);
            var exitCode = kind == OperationKind.ApacheTestConfig && configTestCount++ == 0 ? 1 : 0;
            return Task.FromResult(new ExecutorResult(exitCode, false));
        });

        var result = await executor.ExecuteAsync(
            OperationKind.ApacheApplyConfigSet, "config-set",
            BuildConfigSet(("app.conf", "broken-config\n")), 10, NoOutput,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            [OperationKind.ApacheTestConfig, OperationKind.ApacheTestConfig, OperationKind.ApacheReload],
            operations);
        Assert.Equal("old-config\n", await File.ReadAllTextAsync(
            available, TestContext.Current.CancellationToken));
        Assert.Equal(available, await File.ReadAllTextAsync(
            enabled, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplyConfigSet_ReloadFails_RestoresPreviousFileAndReloadsIt()
    {
        using var layout = new ApacheTestLayout();
        var available = Path.Combine(layout.Available, "app.conf");
        var enabled = Path.Combine(layout.Enabled, "app.conf");
        await File.WriteAllTextAsync(available, "old-config\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(enabled, available, TestContext.Current.CancellationToken);
        var operations = new List<OperationKind>();
        var reloadCount = 0;
        var executor = layout.Build((kind, _, _, _) =>
        {
            operations.Add(kind);
            var exitCode = kind == OperationKind.ApacheReload && reloadCount++ == 0 ? 1 : 0;
            return Task.FromResult(new ExecutorResult(exitCode, false));
        });

        var result = await executor.ExecuteAsync(
            OperationKind.ApacheApplyConfigSet, "config-set",
            BuildConfigSet(("app.conf", "valid-but-reload-rejected\n")), 10, NoOutput,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            [OperationKind.ApacheTestConfig, OperationKind.ApacheReload,
                OperationKind.ApacheTestConfig, OperationKind.ApacheReload],
            operations);
        Assert.Equal("old-config\n", await File.ReadAllTextAsync(
            available, TestContext.Current.CancellationToken));
        Assert.Equal(available, await File.ReadAllTextAsync(
            enabled, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplyConfigSet_CancelledDuringConfigTest_RestoresPreviousSetWithItsOwnBudget()
    {
        using var layout = new ApacheTestLayout();
        var available = Path.Combine(layout.Available, "app.conf");
        var enabled = Path.Combine(layout.Enabled, "app.conf");
        await File.WriteAllTextAsync(available, "old-config\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(enabled, available, TestContext.Current.CancellationToken);
        using var taskCts = new CancellationTokenSource();
        var operations = new List<(OperationKind Kind, bool TokenCancelled)>();
        var executor = layout.Build((kind, _, _, token) =>
        {
            operations.Add((kind, token.IsCancellationRequested));
            if (operations.Count == 1)
            {
                taskCts.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.FromResult(new ExecutorResult(0, false));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(
            OperationKind.ApacheApplyConfigSet, "config-set",
            BuildConfigSet(("app.conf", "new-config\n"), ("new.conf", "partial\n")), 10, NoOutput,
            taskCts.Token));

        Assert.Equal(
            [(OperationKind.ApacheTestConfig, false), (OperationKind.ApacheTestConfig, false),
                (OperationKind.ApacheReload, false)],
            operations);
        Assert.Equal("old-config\n", await File.ReadAllTextAsync(
            available, TestContext.Current.CancellationToken));
        Assert.Equal(available, await File.ReadAllTextAsync(
            enabled, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(layout.Available, "new.conf")));
        Assert.False(File.Exists(Path.Combine(layout.Enabled, "new.conf")));
    }

    [Fact]
    public async Task ApplyConfigSet_InvalidDestination_IsRejectedBeforeFilesystemChanges()
    {
        using var layout = new ApacheTestLayout();
        var privilegedCallCount = 0;
        var executor = layout.Build((_, _, _, _) =>
        {
            privilegedCallCount++;
            return Task.FromResult(new ExecutorResult(0, false));
        });

        var result = await executor.ExecuteAsync(
            OperationKind.ApacheApplyConfigSet, "config-set",
            BuildConfigSet(("../escape.conf", "bad\n")), 10, NoOutput,
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(0, privilegedCallCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.Available));
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.Enabled));
    }

    private static IReadOnlyDictionary<string, string> BuildConfigSet(
        params (string Name, string Content)[] files)
    {
        var manifest = files.ToDictionary(
            file => file.Name,
            file => Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Content)),
            StringComparer.Ordinal);
        return new Dictionary<string, string>
        {
            ["AETHEUS_APACHE_CONFIG_SET_B64"] = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)))
        };
    }

    private sealed class ApacheTestLayout : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("aetheus-apache-test-");

        public ApacheTestLayout()
        {
            Available = Directory.CreateDirectory(Path.Combine(_root.FullName, "sites-available")).FullName;
            Enabled = Directory.CreateDirectory(Path.Combine(_root.FullName, "sites-enabled")).FullName;
        }

        public string Available { get; }
        public string Enabled { get; }

        public ApacheOperationExecutor Build(
            Func<OperationKind, int, Func<string, TaskLogLevel, Task>, CancellationToken, Task<ExecutorResult>> runner)
            => new(Options.Create(new AetheusAgentOptions()), NullLogger<ApacheOperationExecutor>.Instance)
            {
                SitesAvailablePath = Available,
                SitesEnabledPath = Enabled,
                CreateSiteSymbolicLink = (path, target) =>
                {
                    File.WriteAllText(path, target);
                    return new FileInfo(path);
                },
                PrivilegedOperationRunner = runner
            };

        public void Dispose() => _root.Delete(recursive: true);
    }
}
