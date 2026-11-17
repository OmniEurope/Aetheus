// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Recette R-12: enabling, disabling and deleting a site on temporary Apache directories. Every refusal,
/// the exact privileged argv (fixed configtest helper, argv-exact systemctl reload) and every rollback are
/// asserted without Apache, sudo or a symbolic-link privilege: a stand-in "link" is a file holding
/// <see cref="LinkPrefix"/> and its target, which <see cref="ApacheOperationExecutor.ReadLinkTarget"/> reads.</summary>
public sealed class ApacheSiteOperationsTests : IDisposable
{
    private const string LinkPrefix = "LINK->";
    private static readonly string[] ConfigTestArgv = ["-n", "/usr/local/lib/aetheus/aetheus-apache-configtest"];
    private static readonly string[] ReloadArgv = ["-n", "/bin/systemctl", "reload", "apache2.service"];

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("aetheus-apache-site-");
    private readonly string _available;
    private readonly string _enabled;

    public ApacheSiteOperationsTests()
    {
        _available = Directory.CreateDirectory(Path.Combine(_root.FullName, "sites-available")).FullName;
        _enabled = Directory.CreateDirectory(Path.Combine(_root.FullName, "sites-enabled")).FullName;
    }

    public void Dispose() => _root.Delete(recursive: true);

    private ApacheOperationExecutor Build(ProcessRunCalls calls, bool isLinux = true) =>
        new(Options.Create(new AetheusAgentOptions()), NullLogger<ApacheOperationExecutor>.Instance)
        {
            IsLinux = isLinux,
            SitesAvailablePath = _available,
            SitesEnabledPath = _enabled,
            RunProcessWithLoggerAsync = calls.RunAsync,
            CreateSiteSymbolicLink = (link, target) =>
            {
                File.WriteAllText(link, LinkPrefix + target);
                return new FileInfo(link);
            },
            ReadLinkTarget = path => File.Exists(path) && File.ReadAllText(path) is var text && text.StartsWith(LinkPrefix, StringComparison.Ordinal)
                ? text[LinkPrefix.Length..]
                : null
        };

    private static Task<ExecutorResult> Run(ApacheOperationExecutor executor, OperationKind kind, string site, OutputLog log) =>
        executor.ExecuteAsync(kind, site, new Dictionary<string, string>(), 30, log.Write, TestContext.Current.CancellationToken);

    private string Available(string site, string content = "<VirtualHost *:443>\n</VirtualHost>\n")
    {
        var path = Path.Combine(_available, site);
        File.WriteAllText(path, content);
        return path;
    }

    private string Link(string site, string target)
    {
        var path = Path.Combine(_enabled, site);
        File.WriteAllText(path, LinkPrefix + target);
        return path;
    }

    // --- Validation ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("site.conf", true)]
    [InlineData("aetheus-api.sonytumen.com-ssl.conf", true)]
    [InlineData("000-default.conf", true)]
    [InlineData("../sites-enabled/site.conf", false)]
    [InlineData("/etc/passwd.conf", false)]
    [InlineData("sub\\site.conf", false)]
    [InlineData("..", false)]
    [InlineData(".site.conf", false)]
    [InlineData("site", false)]
    [InlineData("site.conf\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAcceptedSiteFile_TakesOnlyABareConfName(string? name, bool expected) =>
        Assert.Equal(expected, ApacheSiteOperations.IsAcceptedSiteFile(name));

    [Theory]
    [InlineData(OperationKind.ApacheEnableSite)]
    [InlineData(OperationKind.ApacheDisableSite)]
    [InlineData(OperationKind.ApacheDeleteSite)]
    public async Task EveryOperation_RefusesATraversalName_BeforeTouchingAnythingOrRunningAProcess(OperationKind kind)
    {
        var outside = Path.Combine(_root.FullName, "outside.conf");
        File.WriteAllText(outside, "keep");
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Run(Build(calls), kind, "../outside.conf", log);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(log.Has("Invalid Apache site file name", TaskLogLevel.Error));
        Assert.Equal("keep", File.ReadAllText(outside));
        Assert.Empty(calls.Runs);
    }

    [Theory]
    [InlineData(OperationKind.ApacheEnableSite)]
    [InlineData(OperationKind.ApacheDisableSite)]
    [InlineData(OperationKind.ApacheDeleteSite)]
    public async Task EveryOperation_RefusesASiteThatIsNotARegularFileOfSitesAvailable(OperationKind kind)
    {
        var calls = new ProcessRunCalls();
        var log = new OutputLog();
        // A link inside sites-available is not a site file either.
        File.WriteAllText(Path.Combine(_available, "linked.conf"), LinkPrefix + "/etc/shadow");

        Assert.Equal(-1, (await Run(Build(calls), kind, "missing.conf", log)).ExitCode);
        Assert.Equal(-1, (await Run(Build(calls), kind, "linked.conf", log)).ExitCode);
        Assert.True(log.Has("is not a regular file of sites-available", TaskLogLevel.Error));
        Assert.Empty(calls.Runs);
    }

    [Fact]
    public async Task SiteOperations_AreLinuxOnly()
    {
        Available("site.conf");
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Run(Build(calls, isLinux: false), OperationKind.ApacheEnableSite, "site.conf", log);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(_enabled, "site.conf")));
        Assert.Empty(calls.Runs);
    }

    // --- Enable ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Enable_LinksTheSite_ThenRunsTheConfigtestHelperAndTheExactReload()
    {
        var available = Available("site.conf");
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.ApacheEnableSite, "site.conf", log);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(LinkPrefix + available, File.ReadAllText(Path.Combine(_enabled, "site.conf")));
        Assert.Equal([ConfigTestArgv, ReloadArgv], calls.Runs.Select(run => run.Argv));
    }

    [Fact]
    public async Task Enable_RemovesTheLinkAndDoesNotReload_WhenApacheRejectsTheConfiguration()
    {
        Available("site.conf");
        var calls = new ProcessRunCalls(index => new ExecutorResult(index == 0 ? 1 : 0, false));
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.ApacheEnableSite, "site.conf", log);

        Assert.Equal(1, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(_enabled, "site.conf")));
        Assert.Equal([ConfigTestArgv], calls.Runs.Select(run => run.Argv));
        Assert.True(log.Has("Apache config test failed for site.conf", TaskLogLevel.Error));
    }

    [Fact]
    public async Task Enable_RemovesTheLinkAndReloadsThePreviousConfiguration_WhenTheReloadFails()
    {
        Available("site.conf");
        var calls = new ProcessRunCalls(index => new ExecutorResult(index == 1 ? 1 : 0, false));
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.ApacheEnableSite, "site.conf", log);

        Assert.Equal(1, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(_enabled, "site.conf")));
        Assert.Equal([ConfigTestArgv, ReloadArgv, ReloadArgv], calls.Runs.Select(run => run.Argv));
    }

    [Fact]
    public async Task Enable_LeavesAForeignEntryOfTheSameNameAlone()
    {
        Available("site.conf");
        var foreign = Path.Combine(_enabled, "site.conf");
        File.WriteAllText(foreign, "<VirtualHost *:80>\n</VirtualHost>\n");
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.ApacheEnableSite, "site.conf", log);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("<VirtualHost *:80>\n</VirtualHost>\n", File.ReadAllText(foreign));
        Assert.Empty(calls.Runs);
    }

    // --- Disable ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Disable_RemovesTheLink_ThenConfigtestAndReload()
    {
        var available = Available("aetheus-api.sonytumen.com-ssl.conf");
        var link = Link("aetheus-api.sonytumen.com-ssl.conf", available);
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.ApacheDisableSite, "aetheus-api.sonytumen.com-ssl.conf", log);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(link));
        Assert.True(File.Exists(available));
        Assert.Equal([ConfigTestArgv, ReloadArgv], calls.Runs.Select(run => run.Argv));
    }

    [Fact]
    public async Task Disable_FollowsARelativeLinkAsA2ensiteWritesIt()
    {
        Available("site.conf");
        var link = Link("site.conf", "../sites-available/site.conf");
        var calls = new ProcessRunCalls();

        var result = await Run(Build(calls), OperationKind.ApacheDisableSite, "site.conf", new OutputLog());

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(link));
    }

    [Fact]
    public async Task Disable_PutsTheLinkBack_WhenApacheRejectsTheConfigurationWithoutTheSite()
    {
        var available = Available("site.conf");
        var link = Link("site.conf", available);
        var calls = new ProcessRunCalls(index => new ExecutorResult(index == 0 ? 1 : 0, false));
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.ApacheDisableSite, "site.conf", log);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(LinkPrefix + available, File.ReadAllText(link));
        Assert.Equal([ConfigTestArgv], calls.Runs.Select(run => run.Argv));
    }

    [Fact]
    public async Task Disable_LeavesARegularFileOrALinkToAnotherFileInSitesEnabled()
    {
        Available("site.conf");
        Available("other.conf");
        var regular = Path.Combine(_enabled, "site.conf");
        File.WriteAllText(regular, "<VirtualHost *:80>\n</VirtualHost>\n");
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        Assert.Equal(-1, (await Run(Build(calls), OperationKind.ApacheDisableSite, "site.conf", log)).ExitCode);
        File.Delete(regular);
        var elsewhere = Link("site.conf", Path.Combine(_available, "other.conf"));
        Assert.Equal(-1, (await Run(Build(calls), OperationKind.ApacheDisableSite, "site.conf", log)).ExitCode);

        Assert.True(File.Exists(elsewhere));
        Assert.True(log.Has("is not a link to", TaskLogLevel.Error));
        Assert.Empty(calls.Runs);
    }

    // --- Delete ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheFileOfADisabledSite_AfterAConfigtest_WithoutReloading()
    {
        var available = Available("aetheus-api.sonytumen.com-ssl.conf");
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.ApacheDeleteSite, "aetheus-api.sonytumen.com-ssl.conf", log);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(available));
        Assert.Equal([ConfigTestArgv], calls.Runs.Select(run => run.Argv));
    }

    [Fact]
    public async Task Delete_RefusesAnEnabledSite_WhateverTheNameOfItsLink()
    {
        var available = Available("site.conf");
        Link("000-site.conf", available);
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.ApacheDeleteSite, "site.conf", log);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(File.Exists(available));
        Assert.True(log.Has("disable it before deleting it", TaskLogLevel.Error));
        Assert.Empty(calls.Runs);
    }

    [Fact]
    public async Task Delete_WritesTheFileBack_WhenApacheRejectsTheConfigurationWithoutIt()
    {
        var available = Available("included.conf", "# included elsewhere\n");
        var calls = new ProcessRunCalls(_ => new ExecutorResult(1, false));
        var log = new OutputLog();

        var result = await Run(Build(calls), OperationKind.ApacheDeleteSite, "included.conf", log);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("# included elsewhere\n", File.ReadAllText(available));
        Assert.True(log.Has("the file was written back", TaskLogLevel.Error));
    }

    // --- Remove several sites (R-26) ---------------------------------------------------------------------

    private static Task<IReadOnlyDictionary<string, string?>> RemoveSites(ApacheOperationExecutor executor, OutputLog log, params string[] sites) =>
        executor.RemoveSitesAsync(sites, 30, log.Write, TestContext.Current.CancellationToken);

    [Fact]
    public async Task RemoveSites_TakesTwoSitesOutTogether_WithOneConfigtestAndOneReload_WhenTheSecondOneIsAlreadyBroken()
    {
        var first = Available("first-ssl.conf");
        var second = Available("second-ssl.conf", "SSLCertificateFile /etc/letsencrypt/live/second/fullchain.pem\n");
        var firstLink = Link("first-ssl.conf", first);
        var secondLink = Link("second-ssl.conf", second);
        // Apache stands in here: its certificate is gone for "second", so the test fails while that site is enabled.
        var calls = new ProcessRunCalls(index => new ExecutorResult(index == 0 && File.Exists(secondLink) ? 1 : 0, false));
        var log = new OutputLog();

        var results = await RemoveSites(Build(calls), log, "first-ssl.conf", "second-ssl.conf");

        Assert.All(results.Values, reason => Assert.Null(reason));
        Assert.False(File.Exists(firstLink));
        Assert.False(File.Exists(secondLink));
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.Equal([ConfigTestArgv, ReloadArgv], calls.Runs.Select(run => run.Argv));
    }

    [Fact]
    public async Task RemoveSites_PutsEverythingBack_AndNamesTheOtherSiteApacheBlames()
    {
        var first = Available("first-ssl.conf", "first\n");
        var second = Available("second-ssl.conf", "second\n");
        var firstLink = Link("first-ssl.conf", first);
        var secondLink = Link("second-ssl.conf", second);
        // A site that is not being removed breaks the configuration whatever happens to the others.
        var other = Available("other-ssl.conf");
        Link("other-ssl.conf", other);
        var calls = new ProcessRunCalls(_ => new ExecutorResult(1, false),
            [$"AH00526: Syntax error on line 12 of {_enabled}/other-ssl.conf:", "SSLCertificateFile: file '/etc/letsencrypt/live/other/fullchain.pem' does not exist or is empty"]);
        var log = new OutputLog();

        var results = await RemoveSites(Build(calls), log, "first-ssl.conf", "second-ssl.conf");

        Assert.Equal(["first-ssl.conf", "second-ssl.conf"], results.Keys.Order());
        Assert.All(results.Values, reason =>
        {
            Assert.Contains("other-ssl.conf", reason, StringComparison.Ordinal);
            Assert.Contains("not one of the sites being removed", reason, StringComparison.Ordinal);
            Assert.Contains("Fix or disable other-ssl.conf", reason, StringComparison.Ordinal);
        });
        Assert.Equal("first\n", File.ReadAllText(first));
        Assert.Equal("second\n", File.ReadAllText(second));
        Assert.Equal(LinkPrefix + first, File.ReadAllText(firstLink));
        Assert.Equal(LinkPrefix + second, File.ReadAllText(secondLink));
        Assert.Equal([ConfigTestArgv], calls.Runs.Select(run => run.Argv));
    }

    [Fact]
    public async Task RemoveSites_PutsEverythingBack_WhenTheConfigurationFailsWithoutNamingAnotherSite()
    {
        var first = Available("first.conf", "first\n");
        var firstLink = Link("first.conf", first);
        var calls = new ProcessRunCalls(_ => new ExecutorResult(1, false));

        var results = await RemoveSites(Build(calls), new OutputLog(), "first.conf");

        Assert.Contains("put back", results["first.conf"], StringComparison.Ordinal);
        Assert.Equal("first\n", File.ReadAllText(first));
        Assert.Equal(LinkPrefix + first, File.ReadAllText(firstLink));
    }

    [Fact]
    public async Task RemoveSites_PutsTheSitesBack_AndReloadsThePreviousConfiguration_WhenTheReloadFails()
    {
        var first = Available("first.conf", "first\n");
        var firstLink = Link("first.conf", first);
        var calls = new ProcessRunCalls(index => new ExecutorResult(index == 1 ? 1 : 0, false));

        var results = await RemoveSites(Build(calls), new OutputLog(), "first.conf");

        Assert.Contains("could not reload", results["first.conf"], StringComparison.Ordinal);
        Assert.Equal("first\n", File.ReadAllText(first));
        Assert.Equal(LinkPrefix + first, File.ReadAllText(firstLink));
        Assert.Equal([ConfigTestArgv, ReloadArgv, ReloadArgv], calls.Runs.Select(run => run.Argv));
    }

    [Fact]
    public async Task RemoveSites_LeavesASiteWhosePreconditionFails_AndRemovesTheOthers()
    {
        var kept = Available("kept.conf");
        var foreignEntry = Path.Combine(_enabled, "kept.conf");
        File.WriteAllText(foreignEntry, "<VirtualHost *:80>\n</VirtualHost>\n");
        var gone = Available("gone.conf");
        Link("gone.conf", gone);
        var calls = new ProcessRunCalls();

        var results = await RemoveSites(Build(calls), new OutputLog(), "kept.conf", "gone.conf");

        Assert.Contains("is not a link to", results["kept.conf"], StringComparison.Ordinal);
        Assert.Null(results.GetValueOrDefault("gone.conf"));
        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(foreignEntry));
        Assert.False(File.Exists(gone));
        Assert.Equal([ConfigTestArgv, ReloadArgv], calls.Runs.Select(run => run.Argv));
    }

    [Fact]
    public async Task RemoveSites_RunsNothing_WhenNoSiteCouldBeTakenOut()
    {
        var calls = new ProcessRunCalls();

        var results = await RemoveSites(Build(calls), new OutputLog(), "missing.conf");

        Assert.Contains("is not a regular file of sites-available", results["missing.conf"], StringComparison.Ordinal);
        Assert.Empty(calls.Runs);
    }
}
