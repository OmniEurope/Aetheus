// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Saving a vhost file and applying a reverse-proxy vhost, on temporary Apache directories: the
/// written bytes, every refusal, the site enabling and the rollback of a site that Apache refused to
/// reload are asserted without Apache, sudo or a symbolic-link privilege.</summary>
public sealed class ApacheOperationExecutorConfigTests : IDisposable
{
    private const string B64Variable = "AETHEUS_APACHE_CONFIG_B64";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("aetheus-apache-config-");
    private readonly string _available;
    private readonly string _enabled;
    private readonly List<(string Link, string Target)> _links = [];

    public ApacheOperationExecutorConfigTests()
    {
        _available = Path.Combine(_root.FullName, "sites-available");
        _enabled = Path.Combine(_root.FullName, "sites-enabled");
        Directory.CreateDirectory(_available);
        Directory.CreateDirectory(_enabled);
    }

    public void Dispose() => _root.Delete(recursive: true);

    private ApacheOperationExecutor Build(
        ProcessRunCalls? calls = null, bool isLinux = true, Func<string, string, FileSystemInfo>? link = null) =>
        new(
            Options.Create(new AetheusAgentOptions()),
            NullLogger<ApacheOperationExecutor>.Instance)
        {
            IsLinux = isLinux,
            SitesAvailablePath = _available,
            SitesEnabledPath = _enabled,
            RunProcessWithLoggerAsync = (calls ?? new ProcessRunCalls()).RunAsync,
            CreateSiteSymbolicLink = link ?? RecordingLink
        };

    // A symbolic link needs a privilege Windows does not grant by default: the stand-in records the call and
    // leaves a regular file where the link would be, which is all the executor looks at.
    private FileSystemInfo RecordingLink(string linkPath, string target)
    {
        _links.Add((linkPath, target));
        File.WriteAllText(linkPath, target);
        return new FileInfo(linkPath);
    }

    private static Dictionary<string, string> Config(string content) =>
        new(StringComparer.Ordinal) { [B64Variable] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) };

    private static Task<ExecutorResult> Save(
        ApacheOperationExecutor executor, string site, Dictionary<string, string> env, OutputLog log) =>
        executor.ExecuteAsync(
            OperationKind.ApacheSaveConfig, site, env, 60, log.Write, TestContext.Current.CancellationToken);

    private static Task<ExecutorResult> Proxy(
        ApacheOperationExecutor executor, string site, Dictionary<string, string> env, OutputLog log) =>
        executor.ExecuteAsync(
            OperationKind.ApacheConfigureProxy, site, env, 60, log.Write, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SaveConfig_WritesTheDecodedBytesToTheNamedSite_AndReportsTheirCount()
    {
        var log = new OutputLog();

        var result = await Save(Build(), "app.conf", Config("<VirtualHost *:80/>"), log);

        Assert.Equal(0, result.ExitCode);
        var destination = Path.Combine(_available, "app.conf");
        Assert.Equal("<VirtualHost *:80/>", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        Assert.True(log.Has($"Wrote 19 bytes to {destination}", TaskLogLevel.Info));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveConfig_WithoutThePayload_FailsAndWritesNothing(bool emptyValue)
    {
        var env = emptyValue ? new Dictionary<string, string> { [B64Variable] = "" } : [];
        var log = new OutputLog();

        var result = await Save(Build(), "app.conf", env, log);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(log.Has($"Missing {B64Variable}", TaskLogLevel.Error));
        Assert.Empty(Directory.GetFiles(_available));
    }

    [Fact]
    public async Task SaveConfig_WhenTheSitesDirectoryIsMissing_Fails()
    {
        Directory.Delete(_available);
        var log = new OutputLog();

        var result = await Save(Build(), "app.conf", Config("x"), log);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(log.Has("not found - Apache may not be installed", TaskLogLevel.Error));
    }

    [Theory]
    [InlineData("../escaped.conf")]
    [InlineData("sub/../../escaped.conf")]
    public async Task SaveConfig_RefusesASiteNameThatEscapesTheSitesDirectory(string site)
    {
        var log = new OutputLog();

        var result = await Save(Build(), site, Config("x"), log);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(log.Has("Path traversal blocked", TaskLogLevel.Error));
        Assert.False(File.Exists(Path.Combine(_root.FullName, "escaped.conf")));
    }

    [Fact]
    public async Task SaveConfig_RefusesAPayloadThatIsNotBase64_AndWritesNothing()
    {
        var env = new Dictionary<string, string> { [B64Variable] = "not base64 !" };
        var log = new OutputLog();

        var result = await Save(Build(), "app.conf", env, log);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(log.Has("Invalid base64 content", TaskLogLevel.Error));
        Assert.Empty(Directory.GetFiles(_available));
    }

    [Fact]
    public async Task SaveConfig_WhenTheDestinationCannotBeWritten_ReportsPermissionDenied()
    {
        Directory.CreateDirectory(Path.Combine(_available, "app.conf"));
        var log = new OutputLog();

        var result = await Save(Build(), "app.conf", Config("x"), log);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(log.Has("Permission denied writing to", TaskLogLevel.Error));
    }

    [Fact]
    public async Task ConfigureProxy_OutsideLinux_RefusesWithoutWritingAnything()
    {
        var log = new OutputLog();

        var result = await Proxy(Build(isLinux: false), "app.conf", Config("x"), log);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(log.Has("only supported on Linux", TaskLogLevel.Error));
        Assert.Empty(Directory.GetFiles(_available));
    }

    [Fact]
    public async Task ConfigureProxy_WhenTheVhostCannotBeSaved_StopsBeforeEnablingOrReloading()
    {
        var calls = new ProcessRunCalls();

        var result = await Proxy(Build(calls), "app.conf", [], new OutputLog());

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(_links);
        Assert.Empty(calls.Runs);
    }

    [Fact]
    public async Task ConfigureProxy_WhenTheEnabledDirectoryIsMissing_FailsAfterSavingAndBeforeReloading()
    {
        Directory.Delete(_enabled);
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Proxy(Build(calls), "app.conf", Config("x"), log);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(log.Has("not found - Apache may not be installed", TaskLogLevel.Error));
        Assert.True(File.Exists(Path.Combine(_available, "app.conf")));
        Assert.Empty(calls.Runs);
    }

    [Fact]
    public async Task ConfigureProxy_SavesEnablesAndReloads_KeepingTheSiteWhenTheReloadSucceeds()
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Proxy(Build(calls), "app.conf", Config("vhost"), log);

        Assert.Equal(0, result.ExitCode);
        var linkPath = Path.Combine(_enabled, "app.conf");
        var sourcePath = Path.Combine(_available, "app.conf");
        Assert.Equal([(linkPath, sourcePath)], _links);
        Assert.True(File.Exists(linkPath));
        Assert.True(log.Has($"Enabled site: {linkPath} -> {sourcePath}", TaskLogLevel.Info));
        Assert.Equal(["-n", "/bin/systemctl", "reload", "apache2.service"], Assert.Single(calls.Runs).Argv);
    }

    [Fact]
    public async Task ConfigureProxy_ReplacesAnExistingLinkInsteadOfFailing()
    {
        var linkPath = Path.Combine(_enabled, "app.conf");
        await File.WriteAllTextAsync(linkPath, "stale", TestContext.Current.CancellationToken);

        var result = await Proxy(Build(), "app.conf", Config("vhost"), new OutputLog());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Path.Combine(_available, "app.conf"), await File.ReadAllTextAsync(linkPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConfigureProxy_WhenTheLinkCannotBeCreated_FailsWithoutReloading()
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();
        var executor = Build(calls, link: (_, _) => throw new IOException("disk full"));

        var result = await Proxy(executor, "app.conf", Config("vhost"), log);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(log.Has("Could not enable site", TaskLogLevel.Error));
        Assert.True(log.Has("disk full", TaskLogLevel.Error));
        Assert.Empty(calls.Runs);
    }

    [Fact]
    public async Task ConfigureProxy_WhenApacheRefusesTheReload_RemovesTheEnabledSiteAndReturnsTheReloadResult()
    {
        var calls = new ProcessRunCalls(_ => new ExecutorResult(1, false));
        var log = new OutputLog();

        var result = await Proxy(Build(calls), "app.conf", Config("broken"), log);

        Assert.Equal(1, result.ExitCode);
        var linkPath = Path.Combine(_enabled, "app.conf");
        Assert.False(File.Exists(linkPath));
        Assert.True(File.Exists(Path.Combine(_available, "app.conf")));
        Assert.True(log.Has($"Reload failed - disabled the new site (removed {linkPath})", TaskLogLevel.Warning));
    }

    [Fact]
    public async Task ConfigureProxy_WhenTheReloadFailsAndTheSiteCannotBeRemoved_TellsTheOperatorToDisableItByHand()
    {
        var calls = new ProcessRunCalls(_ => new ExecutorResult(1, false));
        var log = new OutputLog();
        // A non-empty directory where the link should be: it exists, and a file delete is refused on it.
        var executor = Build(calls, link: (linkPath, _) =>
        {
            Directory.CreateDirectory(linkPath);
            File.WriteAllText(Path.Combine(linkPath, "keep"), "x");
            return new DirectoryInfo(linkPath);
        });

        var result = await Proxy(executor, "app.conf", Config("broken"), log);

        Assert.Equal(1, result.ExitCode);
        Assert.True(log.Has("could not remove the symlink", TaskLogLevel.Error));
        Assert.True(Directory.Exists(Path.Combine(_enabled, "app.conf")));
    }
}
