// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Recette R-26: a certificate an enabled Apache site still names is neither revoked nor deleted, because
/// Apache would then fail its configuration test for the whole server. The refusal comes before certbot runs and says
/// which site(s) and what to do. The enabled sites are simulated in memory: these tests prove the decision, the real
/// sites-enabled reading is the same code the project deletion uses (<see cref="ProjectResourcesOperationExecutorTests"/>).</summary>
public sealed class CertbotLineageInUseTests
{
    private const string Lineage = "app.example.org";
    private const string SitesEnabled = "/etc/apache2/sites-enabled";
    private const string ManageHelper = "/usr/local/lib/aetheus/aetheus-certbot-manage";

    private readonly Dictionary<string, string> _enabledSites = new(StringComparer.Ordinal)
    {
        ["000-default.conf"] = "<VirtualHost *:80>\n  DocumentRoot /var/www/html\n</VirtualHost>",
    };
    private readonly Dictionary<string, string> _rootOnlySites = new(StringComparer.Ordinal);
    private bool _enabledDirectoryUnreadable;

    private CertbotOperationExecutor Build(ProcessRunCalls calls) =>
        new(Options.Create(new AetheusAgentOptions { MinTimeoutSeconds = 5, MaxTimeoutSeconds = 100 }),
            NullLogger<CertbotOperationExecutor>.Instance)
        {
            IsLinux = true,
            RunProcessAsync = calls.RunAsync,
            EnabledSites = new ApacheEnabledSites(
                SitesEnabled,
                path => _enabledSites.GetValueOrDefault(Path.GetFileName(path)),
                _ => null,
                _ => _enabledDirectoryUnreadable ? null : _enabledSites.Keys.Concat(_rootOnlySites.Keys).ToHashSet(StringComparer.Ordinal),
                new ApacheSiteFileReader(NullLogger.Instance)
                {
                    RunHelperAsync = (_, input, _, _) =>
                    {
                        var file = Path.GetFileName(input.TrimEnd('\n'));
                        return Task.FromResult(_rootOnlySites.TryGetValue(file, out var text)
                            ? (new ExecutorResult(0, false), (IReadOnlyList<string>)[.. text.Split('\n'), "[process] exit-code=0"])
                            : (new ExecutorResult(1, false), (IReadOnlyList<string>)["[stderr] not readable", "[process] exit-code=1"]));
                    },
                }),
        };

    private static string Uses(string lineage) => $"<VirtualHost *:443>\n  SSLCertificateFile /etc/letsencrypt/live/{lineage}/fullchain.pem\n</VirtualHost>";

    [Theory]
    [InlineData(OperationKind.CertbotDelete)]
    [InlineData(OperationKind.CertbotRevoke)]
    public async Task ADeletionOrRevocation_IsRefused_WhileAnEnabledSiteUsesTheCertificate_AndNamesTheSiteAndTheWayOut(OperationKind kind)
    {
        _enabledSites["app-ssl.conf"] = Uses(Lineage);
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Build(calls).ExecuteAsync(kind, Lineage, 60, log.Write, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        var message = Assert.Single(log.Lines, line => line.Level == TaskLogLevel.Error).Message;
        Assert.Contains("app-ssl.conf", message, StringComparison.Ordinal);
        Assert.Contains("was not deleted", message, StringComparison.Ordinal);
        Assert.Contains("Disable the site(s) first", message, StringComparison.Ordinal);
        Assert.Contains("delete the project", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EverySiteThatUsesTheCertificate_IsNamed()
    {
        _enabledSites["app-ssl.conf"] = Uses(Lineage);
        _enabledSites["app-www-ssl.conf"] = Uses(Lineage);
        var log = new OutputLog();

        await Build(new ProcessRunCalls()).ExecuteAsync(OperationKind.CertbotDelete, Lineage, 60, log.Write, TestContext.Current.CancellationToken);

        var message = Assert.Single(log.Lines, line => line.Level == TaskLogLevel.Error).Message;
        Assert.Contains("app-ssl.conf, app-www-ssl.conf", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OperationKind.CertbotDelete, "delete")]
    [InlineData(OperationKind.CertbotRevoke, "revoke")]
    public async Task ADeletionOrRevocation_Proceeds_WhenNoEnabledSiteUsesTheCertificate(OperationKind kind, string verb)
    {
        // A site of the same host name prefix or of another lineage is not a use of this one.
        _enabledSites["other-ssl.conf"] = Uses("other.example.org");
        _enabledSites["app-new-ssl.conf"] = Uses(Lineage + "-0001");
        var calls = new ProcessRunCalls();

        var result = await Build(calls).ExecuteAsync(kind, Lineage, 60, new OutputLog().Write, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["-n", ManageHelper, verb, Lineage], Assert.Single(calls.Runs).Argv);
    }

    [Fact]
    public async Task ARenewal_IsNotBlockedByASiteThatUsesTheCertificate()
    {
        _enabledSites["app-ssl.conf"] = Uses(Lineage);
        var calls = new ProcessRunCalls();

        var result = await Build(calls).ExecuteAsync(OperationKind.CertbotRenew, Lineage, 60, new OutputLog().Write, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Single(calls.Runs);
    }

    [Fact]
    public async Task ASiteTheAgentCanOnlyReadThroughTheHelper_IsStillSeenAsAUse()
    {
        _rootOnlySites["app-le-ssl.conf"] = Uses(Lineage);
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Build(calls).ExecuteAsync(OperationKind.CertbotDelete, Lineage, 60, log.Write, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.Contains("app-le-ssl.conf", Assert.Single(log.Lines, line => line.Level == TaskLogLevel.Error).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASiteThatCannotBeReadAtAll_RefusesTheDeletion_InsteadOfGuessingItIsSafe()
    {
        // Listed, but neither the direct read nor the helper gives its text.
        var calls = new ProcessRunCalls();
        var log = new OutputLog();
        var unreadable = new CertbotOperationExecutor(
            Options.Create(new AetheusAgentOptions { MinTimeoutSeconds = 5, MaxTimeoutSeconds = 100 }), NullLogger<CertbotOperationExecutor>.Instance)
        {
            IsLinux = true,
            RunProcessAsync = calls.RunAsync,
            EnabledSites = new ApacheEnabledSites(SitesEnabled, _ => null, _ => null,
                _ => new HashSet<string>(["mystery-ssl.conf"], StringComparer.Ordinal),
                new ApacheSiteFileReader(NullLogger.Instance)
                {
                    RunHelperAsync = (_, _, _, _) => Task.FromResult((new ExecutorResult(1, false), (IReadOnlyList<string>)["[stderr] permission denied", "[process] exit-code=1"])),
                }),
        };

        var result = await unreadable.ExecuteAsync(OperationKind.CertbotDelete, Lineage, 60, log.Write, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        var message = Assert.Single(log.Lines, line => line.Level == TaskLogLevel.Error).Message;
        Assert.Contains("mystery-ssl.conf cannot be read", message, StringComparison.Ordinal);
        Assert.Contains("permission denied", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableSitesEnabledDirectory_RefusesTheDeletion()
    {
        _enabledDirectoryUnreadable = true;
        var calls = new ProcessRunCalls();
        var log = new OutputLog();

        var result = await Build(calls).ExecuteAsync(OperationKind.CertbotDelete, Lineage, 60, log.Write, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(calls.Runs);
        Assert.Contains($"{SitesEnabled} cannot be read", Assert.Single(log.Lines, line => line.Level == TaskLogLevel.Error).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADirectoryWithNoEnabledSite_LetsTheDeletionThrough()
    {
        _enabledSites.Clear();
        var calls = new ProcessRunCalls();

        var result = await Build(calls).ExecuteAsync(OperationKind.CertbotDelete, Lineage, 60, new OutputLog().Write, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Single(calls.Runs);
    }
}
