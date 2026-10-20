// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The compensation of a configuration set that Apache refused: the previous files are put back on disk
/// exactly as they were (or removed when there were none), then tested and reloaded on a budget of their own,
/// and every way the restore itself can fail is said plainly. Real files in temporary sites-available and
/// sites-enabled directories; the site link is a file naming its target, and configtest and reload answer
/// from a script.
/// </summary>
public sealed class ApacheConfigSetRestoreTests : IDisposable
{
    private const string Site = "shop.conf";
    private readonly string _root = Directory.CreateTempSubdirectory("aetheus-apache-set-").FullName;
    private readonly OutputLog _log = new();
    private readonly List<OperationKind> _operations = [];

    public ApacheConfigSetRestoreTests()
    {
        Directory.CreateDirectory(Available);
        Directory.CreateDirectory(Enabled);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Available => Path.Combine(_root, "sites-available");
    private string Enabled => Path.Combine(_root, "sites-enabled");

    [Fact]
    public async Task ARefusedSet_PutsThePreviousFilesBack_ThenTestsAndReloadsThem()
    {
        await File.WriteAllTextAsync(Path.Combine(Available, Site), "previous vhost", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(Enabled, Site), "previous enabled copy", TestContext.Current.CancellationToken);

        var result = await Apply(OperationKind.ApacheTestConfig);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("previous vhost", await File.ReadAllTextAsync(Path.Combine(Available, Site), TestContext.Current.CancellationToken));
        Assert.Equal("previous enabled copy", await File.ReadAllTextAsync(Path.Combine(Enabled, Site), TestContext.Current.CancellationToken));
        Assert.Equal([OperationKind.ApacheTestConfig, OperationKind.ApacheTestConfig, OperationKind.ApacheReload], _operations);
        Assert.True(_log.Has("Apache config test failed; restoring the previous configuration set.", TaskLogLevel.Error));
        Assert.DoesNotContain(_log.Lines, line => line.Message.Contains("manual intervention", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASetThatFailsToReload_OnAHostWithoutThatSite_RemovesItAgain_AndSaysWhenTheRestoreCannotReload()
    {
        var result = await Apply(OperationKind.ApacheReload, OperationKind.ApacheReload);

        Assert.Equal(1, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(Available, Site)));
        Assert.False(File.Exists(Path.Combine(Enabled, Site)));
        Assert.Equal([OperationKind.ApacheTestConfig, OperationKind.ApacheReload, OperationKind.ApacheTestConfig, OperationKind.ApacheReload], _operations);
        Assert.True(_log.Has(
            "Previous Apache configuration passed configtest but could not be reloaded; manual intervention is required.",
            TaskLogLevel.Error));
    }

    [Fact]
    public async Task ARestoredSetThatFailsConfigtest_IsNotReloaded_AndAsksForAnOperator()
    {
        var result = await Apply(OperationKind.ApacheTestConfig, OperationKind.ApacheTestConfig);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal([OperationKind.ApacheTestConfig, OperationKind.ApacheTestConfig], _operations);
        Assert.True(_log.Has(
            "Previous Apache configuration was restored on disk but did not pass configtest; manual intervention is required.",
            TaskLogLevel.Error));
    }

    [Fact]
    public async Task ARestoreThatThrows_IsReportedInsteadOfEscaping()
    {
        var applier = Applier((kind, _, _, _) =>
        {
            _operations.Add(kind);
            return _operations.Count == 1
                ? Task.FromResult(new ExecutorResult(1, false))
                : throw new InvalidOperationException("sudo is not available");
        });

        var result = await applier.ApplyAsync(Payload(), 30, _log.Write, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.True(_log.Has(
            "Could not fully restore the previous Apache configuration: sudo is not available. Manual intervention is required.",
            TaskLogLevel.Error));
    }

    // Each listed operation fails; every other one succeeds.
    private Task<ExecutorResult> Apply(params OperationKind[] failingInOrder)
    {
        var failures = new Queue<OperationKind>(failingInOrder);
        var applier = Applier((kind, _, _, _) =>
        {
            _operations.Add(kind);
            var fails = failures.Count > 0 && failures.Peek() == kind;
            if (fails) failures.Dequeue();
            return Task.FromResult(new ExecutorResult(fails ? 1 : 0, false));
        });
        return applier.ApplyAsync(Payload(), 30, _log.Write, TestContext.Current.CancellationToken);
    }

    private ApacheConfigSetApplier Applier(
        Func<OperationKind, int, Func<string, TaskLogLevel, Task>, CancellationToken, Task<ExecutorResult>> privileged) =>
        new(Available, Enabled, CreateLink, privileged, platformOverride: true);

    private static FileSystemInfo CreateLink(string link, string target)
    {
        File.WriteAllText(link, "-> " + target);
        return new FileInfo(link);
    }

    private static Dictionary<string, string> Payload()
    {
        var manifest = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [Site] = Convert.ToBase64String(Encoding.UTF8.GetBytes("<VirtualHost *:80>\n</VirtualHost>\n")),
        });
        return new Dictionary<string, string>
        {
            ["AETHEUS_APACHE_CONFIG_SET_B64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest)),
        };
    }
}
