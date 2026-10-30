// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary> Recette R-97 (ADR-058): the agent side of a project deletion. It revalidates everything it is sent,
/// runs docker with an argv (never a shell line), removes only what the user confirmed and says resource by
/// resource what happened. The host is simulated: these tests prove the decisions, not a real Docker run. </summary>
public sealed class ProjectResourcesOperationExecutorTests
{
    private const string Host = "app.portfolio.example";
    private const string StateDir = "/var/lib/portfolio-production";

    private static readonly ProjectServerResourceSpec Identity = new()
    {
        Slug = "portfolio",
        ComposeProjects = ["portfolio-prod"],
        Hosts = [Host],
        StateDirectories = [StateDir],
    };

    private static readonly ProjectServerResourceRef Web = new(ProjectServerResourceKind.Container, "portfolio-prod-web-1", "portfolio-prod");
    private static readonly ProjectServerResourceRef Worker = new(ProjectServerResourceKind.Container, "portfolio-prod-worker-1", "portfolio-prod");
    private static readonly ProjectServerResourceRef Data = new(ProjectServerResourceKind.Volume, "portfolio-prod_db", "portfolio-prod");
    private static readonly ProjectServerResourceRef Site = new(ProjectServerResourceKind.VirtualHost, Host + "-ssl.conf", Host);
    private static readonly ProjectServerResourceRef Certificate = new(ProjectServerResourceKind.Certificate, Host, Host);
    private static readonly ProjectServerResourceRef State = new(ProjectServerResourceKind.StateDirectory, StateDir, StateDir);

    private readonly ConcurrentQueue<string> _commands = new();
    private readonly List<string> _output = [];
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { StateDir };
    private readonly Dictionary<string, string[]> _docker = new(StringComparer.Ordinal)
    {
        ["ps"] = ["portfolio-prod-web-1"],
        ["volume ls"] = ["portfolio-prod_db"],
        ["network ls"] = [],
    };
    private readonly Dictionary<string, (int ExitCode, string Line)> _dockerFailures = new(StringComparer.Ordinal);
    private bool _stateIsLink;
    private readonly HashSet<string> _links = new(StringComparer.Ordinal);
    private string _serverUrl = new AetheusAgentOptions().ServerUrl;
    private readonly HashSet<OperationKind> _failingSiteOperations = [];
    private readonly Dictionary<string, string> _enabledSites = new(StringComparer.Ordinal)
    {
        ["000-default.conf"] = "<VirtualHost *:80>\n  DocumentRoot /var/www/html\n</VirtualHost>",
    };
    private readonly Dictionary<string, string> _availableSites = new(StringComparer.Ordinal);
    // Enabled sites the agent account cannot open (certbot writes <site>-le-ssl.conf as root, mode 0600): the direct
    // read gives nothing, and only the root-owned read helper (simulated here by _helperAnswer) hands their text over.
    private readonly Dictionary<string, string> _rootOnlySites = new(StringComparer.Ordinal);
    private (int ExitCode, string Line)? _helperFailure;
    /// <summary>sites-enabled entries that are links, by name, to their absolute target.</summary>
    private readonly Dictionary<string, string> _siteLinks = new(StringComparer.Ordinal);
    private readonly List<string> _helperInputs = [];

    private ProjectResourcesOperationExecutor Executor() => new(
        new ApacheOperationExecutor(Options.Create(new AetheusAgentOptions()), NullLogger<ApacheOperationExecutor>.Instance),
        new CertbotOperationExecutor(Options.Create(new AetheusAgentOptions()), NullLogger<CertbotOperationExecutor>.Instance),
        Options.Create(new AetheusAgentOptions { ServerUrl = _serverUrl }),
        NullLogger<ProjectResourcesOperationExecutor>.Instance)
    {
        IsLinux = true,
        RunDockerAsync = (argv, _) =>
        {
            var line = string.Join(' ', argv);
            _commands.Enqueue("docker " + line);
            // The shape ProcessRunner.CaptureAsync hands back: stdout lines, stderr lines prefixed "[stderr]",
            // then its own "[process] exit-code=N" line.
            if (_dockerFailures.TryGetValue(line, out var failure))
                return Task.FromResult((new ExecutorResult(failure.ExitCode, false),
                    (IReadOnlyList<string>)[$"[stderr] {failure.Line}", $"[process] exit-code={failure.ExitCode}"]));
            var listed = _docker.FirstOrDefault(entry => line.StartsWith(entry.Key + " ", StringComparison.Ordinal)).Value ?? [];
            return Task.FromResult((new ExecutorResult(0, false),
                (IReadOnlyList<string>)[.. listed, "[stderr] WARNING: No swap limit support", "[process] exit-code=0"]));
        },
        RunSiteOperationAsync = (kind, site, _, _, _) =>
        {
            _commands.Enqueue($"{kind} {site}");
            return Task.FromResult(new ExecutorResult(_failingSiteOperations.Contains(kind) ? 1 : 0, false));
        },
        DeleteCertificateAsync = (lineage, _, _, _) =>
        {
            _commands.Enqueue("certbot delete " + lineage);
            return Task.FromResult(new ExecutorResult(0, false));
        },
        ListDirectory = path => path.EndsWith("sites-available", StringComparison.Ordinal)
            ? new HashSet<string>([Host + ".conf", Host + "-ssl.conf", "000-default.conf", .. _availableSites.Keys])
            : path.EndsWith("sites-enabled", StringComparison.Ordinal) ? _enabledSites.Keys.Concat(_rootOnlySites.Keys).ToHashSet()
            : path.EndsWith("/live", StringComparison.Ordinal) ? new HashSet<string>([Host]) : new HashSet<string>(),
        ReadSiteFile = path => path.Contains("/sites-available/", StringComparison.Ordinal)
            ? _availableSites.GetValueOrDefault(Path.GetFileName(path))
            : _enabledSites.GetValueOrDefault(Path.GetFileName(path)),
        LinkTargetOf = path => _siteLinks.GetValueOrDefault(Path.GetFileName(path)),
        SiteReader = new ApacheSiteFileReader(NullLogger.Instance)
        {
            RunHelperAsync = (argv, input, _, _) =>
            {
                Assert.Equal(["-n", "/usr/local/lib/aetheus/aetheus-apache-read-site", "read"], argv);
                _helperInputs.Add(input);
                if (_helperFailure is { } failure)
                    return Task.FromResult((new ExecutorResult(failure.ExitCode, false),
                        (IReadOnlyList<string>)[$"[stderr] {failure.Line}", $"[process] exit-code={failure.ExitCode}"]));
                var file = Path.GetFileName(input.TrimEnd('\n'));
                var lines = _rootOnlySites.TryGetValue(file, out var text) ? text.Split('\n') : [];
                return Task.FromResult((new ExecutorResult(_rootOnlySites.ContainsKey(file) ? 0 : 1, false),
                    (IReadOnlyList<string>)[.. lines, "[process] exit-code=0"]));
            },
        },
        IsRegularFile = _ => true,
        DirectoryExists = _directories.Contains,
        IsLink = path => (_stateIsLink && path == StateDir) || _links.Contains(path),
        DeleteDirectory = path =>
        {
            _commands.Enqueue("delete " + path);
            _directories.Remove(path);
        },
    };

    private Task<ExecutorResult> RunAsync(OperationKind kind, ProjectServerResourceSpec spec, string target = "7") =>
        Executor().ExecuteAsync(kind, target, new Dictionary<string, string> { [ProjectServerResourceSpec.EnvironmentVariable] = spec.Encode() },
            300, (line, _) => { _output.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Discover_ListsWhatTheHostHoldsOfTheIdentity_AndChangesNothing()
    {
        var result = await RunAsync(OperationKind.ProjectResourcesDiscover, Identity);

        Assert.Equal(0, result.ExitCode);
        var report = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        Assert.True(report.Complete);
        Assert.Equal(
            [Web, Data, new(ProjectServerResourceKind.VirtualHost, Host + ".conf", Host), Site, Certificate, State],
            report.Resources.Select(item => new ProjectServerResourceRef(item.Kind, item.Name, item.Scope)));
        Assert.All(report.Resources, item => Assert.Equal(ProjectServerResourceOutcome.Found, item.Outcome));
        Assert.Empty(report.Unreadable);
        Assert.Contains("docker ps -a --filter label=com.docker.compose.project=portfolio-prod --format {{.Names}}", _commands);
        Assert.All(_commands, command => Assert.True(command.StartsWith("docker ps ", StringComparison.Ordinal)
            || command.StartsWith("docker volume ls ", StringComparison.Ordinal) || command.StartsWith("docker network ls ", StringComparison.Ordinal)
            || command.StartsWith("docker image ls ", StringComparison.Ordinal), command));
    }

    [Fact]
    public async Task AnInvalidTarget_OrSpecification_IsRefusedBeforeTheHostIsRead()
    {
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesDiscover, Identity, "7;reboot")).ExitCode);
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesDiscover, Identity with { StateDirectories = ["/"] })).ExitCode);
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesDiscover, Identity with { ComposeProjects = ["x;rm -rf /"] })).ExitCode);
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesDiscover, Identity with { Remove = [Web] })).ExitCode);
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesRemove,
            Identity with { Remove = [new(ProjectServerResourceKind.Volume, "other_db", "other-prod")] })).ExitCode);
        Assert.Empty(_commands);
    }

    [Fact]
    public async Task Remove_RemovesExactlyTheListedResources_AndLeavesWhatAppearedSince()
    {
        _docker["ps"] = ["portfolio-prod-web-1", "portfolio-prod-worker-1"];

        var result = await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [Web, Data, Site, Certificate, State] });

        Assert.Equal(1, result.ExitCode);
        var commands = _commands.Where(command => !command.Contains(" ls ", StringComparison.Ordinal) && !command.StartsWith("docker ps", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            ["docker rm -f portfolio-prod-web-1", "docker volume rm portfolio-prod_db",
             $"ApacheDisableSite {Host}-ssl.conf", $"ApacheDeleteSite {Host}-ssl.conf", $"certbot delete {Host}", $"delete {StateDir}"],
            commands);
        var report = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        Assert.True(report.Complete);
        Assert.Equal(5, report.Resources.Count(item => item.Outcome == ProjectServerResourceOutcome.Removed));
        var left = report.Resources.Where(item => item.Outcome == ProjectServerResourceOutcome.NotListed).ToList();
        Assert.Equal([Worker.Name, Host + ".conf"], left.Select(item => item.Name));
    }

    [Fact]
    public async Task Remove_AFailure_IsReportedOnItsResource_AndTheTaskFails()
    {
        _dockerFailures["volume rm portfolio-prod_db"] = (1, "Error response from daemon: remove portfolio-prod_db: volume is in use");

        var result = await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [Web, Data] });

        Assert.Equal(1, result.ExitCode);
        var report = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        Assert.Equal(ProjectServerResourceOutcome.Removed, Assert.Single(report.Resources, item => item.Name == Web.Name).Outcome);
        var failed = Assert.Single(report.Resources, item => item.Name == Data.Name);
        Assert.Equal(ProjectServerResourceOutcome.Failed, failed.Outcome);
        Assert.Contains("volume is in use", failed.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("[process]", failed.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("[stderr]", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_OfAResourceAlreadyGone_SaysAbsent_AndASymbolicLinkIsNeverFollowed()
    {
        _docker["ps"] = [];
        _stateIsLink = true;

        await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [Web, State] });

        var report = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        Assert.Equal(ProjectServerResourceOutcome.Absent, Assert.Single(report.Resources, item => item.Name == Web.Name).Outcome);
        Assert.Equal(ProjectServerResourceOutcome.Failed, Assert.Single(report.Resources, item => item.Name == StateDir).Outcome);
        Assert.Contains(report.Unreadable, item => item.Kind == ProjectServerResourceKind.StateDirectory);
        Assert.DoesNotContain(_commands, command => command.StartsWith("delete ", StringComparison.Ordinal) || command.StartsWith("docker rm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_KeepsTheCertificate_WhenTheVirtualHostOfItsHostWasNotRemoved()
    {
        _failingSiteOperations.Add(OperationKind.ApacheDisableSite);

        var result = await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [Site, Certificate] });

        Assert.Equal(1, result.ExitCode);
        var report = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        var certificate = Assert.Single(report.Resources, item => item.Kind == ProjectServerResourceKind.Certificate);
        Assert.Equal((ProjectServerResourceOutcome.Failed, "vhost not removed"), (certificate.Outcome, certificate.Reason));
        Assert.DoesNotContain(_commands, command => command.StartsWith("certbot delete", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Host + ".conf", "<VirtualHost *:80>\n  ServerName " + Host + "\n</VirtualHost>", "still enabled")]
    [InlineData("other.conf", "SSLCertificateFile /etc/letsencrypt/live/" + Host + "/fullchain.pem", "other.conf still uses it")]
    public async Task Remove_KeepsTheCertificate_WhileAnEnabledSiteMayStillUseIt(string site, string text, string reason)
    {
        _enabledSites[site] = text;

        await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [Site, Certificate] });

        var report = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        var certificate = Assert.Single(report.Resources, item => item.Kind == ProjectServerResourceKind.Certificate);
        Assert.Equal(ProjectServerResourceOutcome.Failed, certificate.Outcome);
        Assert.Contains(reason, certificate.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(_commands, command => command.StartsWith("certbot delete", StringComparison.Ordinal));
    }

    // Production 2026-10-27 (project W6): the enabled prometheus-le-ssl.conf of another application is root-owned, mode 0600.
    // The agent could not read it, so every certificate of the project was kept "because it may still be used".
    private const string RootOnlySite = "prometheus-le-ssl.conf";

    [Fact]
    public async Task Remove_DeletesTheCertificate_WhenAnUnreadableEnabledSiteReadThroughTheHelperDoesNotUseIt()
    {
        _rootOnlySites[RootOnlySite] = "<VirtualHost *:443>\n  SSLCertificateFile /etc/letsencrypt/live/prometheus.example.com/fullchain.pem\n</VirtualHost>";

        var result = await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [Site, Certificate] });

        Assert.Equal(1, result.ExitCode);
        var certificate = Assert.Single(ProjectServerResourceOutput.Parse(string.Join('\n', _output)).Resources,
            item => item.Kind == ProjectServerResourceKind.Certificate);
        Assert.Equal(ProjectServerResourceOutcome.Removed, certificate.Outcome);
        Assert.Contains("certbot delete " + Host, _commands);
        Assert.Equal(["/etc/apache2/sites-enabled/" + RootOnlySite + "\n"], _helperInputs);
    }

    [Fact]
    public async Task Remove_KeepsTheCertificate_WhenAnUnreadableEnabledSiteReadThroughTheHelperStillUsesIt()
    {
        _rootOnlySites[RootOnlySite] = "SSLCertificateFile /etc/letsencrypt/live/" + Host + "/fullchain.pem";

        await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [Site, Certificate] });

        var certificate = Assert.Single(ProjectServerResourceOutput.Parse(string.Join('\n', _output)).Resources,
            item => item.Kind == ProjectServerResourceKind.Certificate);
        Assert.Equal(ProjectServerResourceOutcome.Failed, certificate.Outcome);
        Assert.Contains(RootOnlySite + " still uses it", certificate.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(_commands, command => command.StartsWith("certbot delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_DeletesTheCertificate_WhenAnUnreadableEnabledSiteIsALinkToAGoneFile()
    {
        // A sites-enabled link left after its sites-available file was removed: Apache cannot load it, and the helper
        // refuses it ("is not a readable regular file"). It must not keep every certificate of the server.
        const string dangling = "prometheus-le-ssl.conf";
        _rootOnlySites[dangling] = "irrelevant";
        _siteLinks[dangling] = "/etc/apache2/sites-available/" + dangling;
        _helperFailure = (1, "aetheus-apache-read-site: /etc/apache2/sites-enabled/" + dangling + " is not a readable regular file");

        await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [Site, Certificate] });

        var certificate = Assert.Single(ProjectServerResourceOutput.Parse(string.Join('\n', _output)).Resources,
            item => item.Kind == ProjectServerResourceKind.Certificate);
        Assert.Equal(ProjectServerResourceOutcome.Removed, certificate.Outcome);
        Assert.Contains(_commands, command => command == "certbot delete " + Host);
    }

    [Fact]
    public async Task Remove_KeepsTheCertificate_WhenAnUnreadableEnabledSiteIsALinkToAFileStillThere()
    {
        _rootOnlySites[RootOnlySite] = "irrelevant";
        _availableSites[RootOnlySite] = "irrelevant";
        _siteLinks[RootOnlySite] = "/etc/apache2/sites-available/" + RootOnlySite;
        _helperFailure = (1, "aetheus-apache-read-site: /etc/apache2/sites-enabled/" + RootOnlySite + " did not open within 10s");

        await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [Site, Certificate] });

        var certificate = Assert.Single(ProjectServerResourceOutput.Parse(string.Join('\n', _output)).Resources,
            item => item.Kind == ProjectServerResourceKind.Certificate);
        Assert.Equal(ProjectServerResourceOutcome.Failed, certificate.Outcome);
        Assert.Contains($"the enabled site {RootOnlySite} cannot be read, so it may still use the certificate", certificate.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(_commands, command => command.StartsWith("certbot delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_KeepsTheCertificate_WithTheReason_WhenTheHelperCannotReadTheSiteEither()
    {
        _rootOnlySites[RootOnlySite] = "irrelevant";
        _helperFailure = (1, "aetheus-apache-read-site: /etc/apache2/sites-enabled/" + RootOnlySite + " resolves outside the Apache site directories: refused");

        await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [Site, Certificate] });

        var certificate = Assert.Single(ProjectServerResourceOutput.Parse(string.Join('\n', _output)).Resources,
            item => item.Kind == ProjectServerResourceKind.Certificate);
        Assert.Equal(ProjectServerResourceOutcome.Failed, certificate.Outcome);
        Assert.Contains($"the enabled site {RootOnlySite} cannot be read, so it may still use the certificate", certificate.Reason, StringComparison.Ordinal);
        Assert.Contains("exit code 1", certificate.Reason, StringComparison.Ordinal);
        Assert.Contains("resolves outside the Apache site directories", certificate.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(_commands, command => command.StartsWith("certbot delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_KeepsTheUpstreamFile_WhenAnUnreadableEnabledSiteReadThroughTheHelperUsesItsDefine()
    {
        _availableSites[UpstreamFile] = UpstreamText;
        _rootOnlySites[RootOnlySite] = "ProxyPass / http://127.0.0.1:${PORTFOLIO_BACK_PORT}/";

        await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { UpstreamConfigurations = [UpstreamPath], Remove = [Upstream] });

        var upstream = Assert.Single(ProjectServerResourceOutput.Parse(string.Join('\n', _output)).Resources,
            item => item.Kind == ProjectServerResourceKind.UpstreamConfiguration);
        Assert.Equal((ProjectServerResourceOutcome.Failed, $"the enabled site {RootOnlySite} still uses its Define PORTFOLIO_BACK_PORT"), (upstream.Outcome, upstream.Reason));
    }

    [Fact]
    public async Task Remove_DeletesTheUpstreamFile_WhenAnUnreadableEnabledSiteReadThroughTheHelperUsesNoDefine()
    {
        _availableSites[UpstreamFile] = UpstreamText;
        _rootOnlySites[RootOnlySite] = "SSLCertificateFile /etc/letsencrypt/live/prometheus.example.com/fullchain.pem";

        await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { UpstreamConfigurations = [UpstreamPath], Remove = [Upstream] });

        var upstream = Assert.Single(ProjectServerResourceOutput.Parse(string.Join('\n', _output)).Resources,
            item => item.Kind == ProjectServerResourceKind.UpstreamConfiguration);
        Assert.Equal(ProjectServerResourceOutcome.Removed, upstream.Outcome);
    }

    [Fact]
    public async Task Remove_DeletesAStateDirectoryInsideTheAgentsProjectsDirectory()
    {
        // R-97: the wizard now writes the state directory inside the directory the agent owns.
        const string agentState = "/var/lib/aetheus-agent/projects/portfolio-production";
        _directories.Add(agentState);
        var state = new ProjectServerResourceRef(ProjectServerResourceKind.StateDirectory, agentState, agentState);

        var result = await RunAsync(OperationKind.ProjectResourcesRemove,
            Identity with { ComposeProjects = [], Hosts = [], StateDirectories = [agentState], Remove = [state] });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"delete {agentState}", _commands);
        var report = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        Assert.Equal(ProjectServerResourceOutcome.Removed, Assert.Single(report.Resources).Outcome);
    }

    [Theory]
    [InlineData("/var/lib/aetheus-agent/projects")]
    [InlineData("/var/lib/aetheus-agent")]
    public async Task Remove_RefusesAStateDirectoryOfTheProjectsDirectory_WhenAParentIsASymbolicLink(string linkedParent)
    {
        const string agentState = "/var/lib/aetheus-agent/projects/portfolio-production";
        _directories.Add(agentState);
        _links.Add(linkedParent);
        var state = new ProjectServerResourceRef(ProjectServerResourceKind.StateDirectory, agentState, agentState);

        var result = await RunAsync(OperationKind.ProjectResourcesRemove,
            Identity with { ComposeProjects = [], Hosts = [], StateDirectories = [agentState], Remove = [state] });

        Assert.Equal(1, result.ExitCode);
        var report = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        Assert.Equal(ProjectServerResourceOutcome.Failed, Assert.Single(report.Resources).Outcome);
        Assert.Contains(report.Unreadable, item => item.Reason.Contains(linkedParent + " is a symbolic link", StringComparison.Ordinal));
        Assert.DoesNotContain(_commands, command => command.StartsWith("delete ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/var/lib/aetheus-agent/w")]
    [InlineData("/var/lib/aetheus-agent/keys")]
    [InlineData("/var/lib/aetheus-agent/projects/../keys")]
    [InlineData("/var/lib/aetheus-agent/projects/other-production")]
    [InlineData("/var/lib/aetheus-agent/projects/portfolio-production/sub")]
    public async Task TheAgent_RefusesAnythingElseUnderItsWorkingDirectory(string directory)
    {
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesDiscover,
            Identity with { ComposeProjects = [], Hosts = [], StateDirectories = [directory] })).ExitCode);
        Assert.Empty(_commands);
    }

    [Fact]
    public async Task TheAgent_TiesTheStateDirectoryAndComposeProject_ToTheProjectSlug()
    {
        // Every value is a valid name, but none is named after the project the task is for.
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesDiscover, Identity with { Slug = "shop" })).ExitCode);
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesDiscover,
            Identity with { StateDirectories = ["/var/lib/redis"], Slug = "redis", ComposeProjects = [] })).ExitCode);
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesDiscover, Identity with { Slug = "" })).ExitCode);
        Assert.Empty(_commands);
    }

    [Fact]
    public async Task TheAgent_RefusesTheControlPlaneNamespace_EvenForAProjectCalledAetheus()
    {
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesDiscover,
            Identity with { Slug = "aetheus", ComposeProjects = ["aetheus-prod"], Hosts = [], StateDirectories = [] })).ExitCode);
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesDiscover,
            Identity with { Slug = "aetheus", ComposeProjects = [], Hosts = [], StateDirectories = ["/var/lib/aetheus-prod"] })).ExitCode);
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesDiscover, Identity with { Hosts = ["aetheus-api.sonytumen.com"] })).ExitCode);
        Assert.Empty(_commands);
    }

    [Fact]
    public async Task TheAgent_RefusesTheHostOfTheControlPlaneItReportsTo()
    {
        _serverUrl = $"https://{Host}:443/";

        var result = await RunAsync(OperationKind.ProjectResourcesDiscover, Identity);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(_output, line => line.Contains(Host, StringComparison.Ordinal) && line.Contains("control plane", StringComparison.Ordinal));
        Assert.Empty(_commands);
    }

    [Fact]
    public async Task Discover_NeverReportsADockerObjectOfTheControlPlaneNamespace_AsTheProjects()
    {
        // A container labelled with the project's Compose project but named like the control plane's.
        _docker["ps"] = ["portfolio-prod-web-1", "aetheus-prod-back-1"];

        await RunAsync(OperationKind.ProjectResourcesDiscover, Identity);

        var report = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        Assert.DoesNotContain(report.Resources, item => item.Name.StartsWith("aetheus", StringComparison.Ordinal));
        Assert.Contains(report.Unreadable, item => item.Reason.Contains("aetheus-prod-back-1", StringComparison.Ordinal));
    }

    private const string UpstreamFile = "000-portfolio-upstream.conf";
    private const string UpstreamPath = "/etc/apache2/sites-available/" + UpstreamFile;
    private const string UpstreamText = "# Managed by the deployment pipeline\nDefine PORTFOLIO_FRONT_PORT 18080\nDefine PORTFOLIO_BACK_PORT 18081\n";
    private static readonly ProjectServerResourceRef Upstream = new(ProjectServerResourceKind.UpstreamConfiguration, UpstreamPath, UpstreamPath);
    private static readonly ProjectServerResourceRef BackImage = new(ProjectServerResourceKind.Image, "portfolio-prod-back-blue:latest", "portfolio-prod");

    [Fact]
    public async Task Discover_ListsTheImagesTheComposeProjectBuilt_AndTheBlueGreenUpstreamFile()
    {
        _docker["image ls"] = ["3f2a9c1b7d4e portfolio-prod-back-blue:latest", "0123456789ab <none>:<none>"];
        _availableSites[UpstreamFile] = UpstreamText;

        var result = await RunAsync(OperationKind.ProjectResourcesDiscover, Identity with { UpstreamConfigurations = [UpstreamPath] });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("docker image ls --filter label=com.docker.compose.project=portfolio-prod --format {{.ID}} {{.Repository}}:{{.Tag}}", _commands);
        var report = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        Assert.Empty(report.Unreadable);
        Assert.Equal([BackImage.Name, "0123456789ab"],
            report.Resources.Where(item => item.Kind == ProjectServerResourceKind.Image).Select(item => item.Name));
        var upstream = Assert.Single(report.Resources, item => item.Kind == ProjectServerResourceKind.UpstreamConfiguration);
        Assert.Equal((UpstreamPath, UpstreamPath), (upstream.Name, upstream.Scope));
    }

    [Theory]
    [InlineData("/etc/apache2/apache2.conf")]
    [InlineData("/etc/apache2/sites-available/000-default.conf")]
    [InlineData("/etc/apache2/sites-available/../apache2.conf")]
    [InlineData("/etc/apache2/sites-enabled/000-portfolio-upstream.conf")]
    [InlineData("/etc/apache2/sites-available/000-aetheus-upstream.conf")]
    public async Task AnUpstreamFile_OutsideSitesAvailable_OrNotTheProjects_IsRefusedBeforeTheHostIsRead(string path)
    {
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesDiscover, Identity with { UpstreamConfigurations = [path] })).ExitCode);
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesRemove, Identity with
        {
            UpstreamConfigurations = [UpstreamPath],
            Remove = [new(ProjectServerResourceKind.UpstreamConfiguration, path, path)],
        })).ExitCode);
        Assert.Empty(_commands);
    }

    [Fact]
    public async Task Remove_DisablesThenDeletesTheUpstreamFile_AfterTheVirtualHosts_AndTheImagesAfterTheContainers()
    {
        _docker["image ls"] = ["3f2a9c1b7d4e portfolio-prod-back-blue:latest"];
        _availableSites[UpstreamFile] = UpstreamText;
        _enabledSites[UpstreamFile] = UpstreamText;

        var result = await RunAsync(OperationKind.ProjectResourcesRemove,
            Identity with { UpstreamConfigurations = [UpstreamPath], Remove = [Upstream, BackImage, Site, Web] });

        var report = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        Assert.All(new[] { Upstream, BackImage, Site, Web }, item => Assert.Equal(ProjectServerResourceOutcome.Removed,
            Assert.Single(report.Resources, line => line.Kind == item.Kind && line.Name == item.Name).Outcome));
        var commands = _commands.Where(command => !command.Contains(" ls ", StringComparison.Ordinal) && !command.StartsWith("docker ps", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            ["docker rm -f portfolio-prod-web-1", $"ApacheDisableSite {Host}-ssl.conf", $"ApacheDeleteSite {Host}-ssl.conf",
             "docker image rm portfolio-prod-back-blue:latest", $"ApacheDisableSite {UpstreamFile}", $"ApacheDeleteSite {UpstreamFile}"],
            commands);
        Assert.Contains("docker container ls -a --filter ancestor=portfolio-prod-back-blue:latest --format {{.Names}}", _commands);
        Assert.DoesNotContain(_commands, command => command.Contains("--force", StringComparison.Ordinal) || command.StartsWith("docker image rm -f", StringComparison.Ordinal));
        // What is left (the data volume, the plain virtual host, the certificate, the state directory) was not confirmed.
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Remove_KeepsTheUpstreamFile_WhileAnEnabledSiteStillUsesItsDefine()
    {
        _availableSites[UpstreamFile] = UpstreamText;
        _enabledSites["other.conf"] = "<VirtualHost *:443>\n  ProxyPass / http://127.0.0.1:${PORTFOLIO_FRONT_PORT}/\n</VirtualHost>";

        var result = await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { UpstreamConfigurations = [UpstreamPath], Remove = [Upstream] });

        Assert.Equal(1, result.ExitCode);
        var upstream = Assert.Single(ProjectServerResourceOutput.Parse(string.Join('\n', _output)).Resources,
            item => item.Kind == ProjectServerResourceKind.UpstreamConfiguration);
        Assert.Equal(ProjectServerResourceOutcome.Failed, upstream.Outcome);
        Assert.Equal("the enabled site other.conf still uses its Define PORTFOLIO_FRONT_PORT", upstream.Reason);
        Assert.DoesNotContain(_commands, command => command.Contains(UpstreamFile, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_KeepsTheUpstreamFile_WhenApacheRefusesItsRemoval()
    {
        _availableSites[UpstreamFile] = UpstreamText;
        _failingSiteOperations.Add(OperationKind.ApacheDeleteSite);

        var result = await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { UpstreamConfigurations = [UpstreamPath], Remove = [Upstream] });

        Assert.Equal(1, result.ExitCode);
        var upstream = Assert.Single(ProjectServerResourceOutput.Parse(string.Join('\n', _output)).Resources,
            item => item.Kind == ProjectServerResourceKind.UpstreamConfiguration);
        Assert.Equal((ProjectServerResourceOutcome.Failed, "the upstream file could not be deleted (exit code 1)"), (upstream.Outcome, upstream.Reason));
    }

    [Fact]
    public async Task Remove_RefusesAnImageAContainerStillUses_AndNeverForcesIt()
    {
        _docker["image ls"] = ["3f2a9c1b7d4e portfolio-prod-back-blue:latest"];
        _docker["container ls"] = ["portfolio-prod-back-green-1"];

        var result = await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [BackImage] });

        Assert.Equal(1, result.ExitCode);
        var image = Assert.Single(ProjectServerResourceOutput.Parse(string.Join('\n', _output)).Resources, item => item.Kind == ProjectServerResourceKind.Image);
        Assert.Equal(ProjectServerResourceOutcome.Failed, image.Outcome);
        Assert.Equal("still used by the container portfolio-prod-back-green-1: left as it is", image.Reason);
        Assert.DoesNotContain(_commands, command => command.StartsWith("docker image rm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_ReportsDockersRefusal_OfAnImage()
    {
        _docker["image ls"] = ["3f2a9c1b7d4e portfolio-prod-back-blue:latest"];
        _dockerFailures["image rm portfolio-prod-back-blue:latest"] = (1, "Error response from daemon: conflict: unable to remove repository reference");

        await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [BackImage] });

        var image = Assert.Single(ProjectServerResourceOutput.Parse(string.Join('\n', _output)).Resources, item => item.Kind == ProjectServerResourceKind.Image);
        Assert.Equal(ProjectServerResourceOutcome.Failed, image.Outcome);
        Assert.Contains("unable to remove repository reference", image.Reason, StringComparison.Ordinal);
    }

    private const string DeploymentsBase = "/var/lib/aetheus-agent/.deployments";
    private const string DeployDir = DeploymentsBase + "/portfolio-prod";
    private static readonly ProjectServerResourceRef Deployment = new(ProjectServerResourceKind.DeploymentDirectory, DeployDir, "portfolio-prod");

    [Fact]
    public async Task Discover_ListsTheRepositoryCopyADeploymentLeft_UnderTheAgentsDeploymentsDirectory()
    {
        // R-85: the generic deploy templates unpack the repository in ${HOME}/.deployments/$COMPOSE_PROJECT.
        _directories.Add(DeployDir);

        var result = await RunAsync(OperationKind.ProjectResourcesDiscover, Identity);

        Assert.Equal(0, result.ExitCode);
        var report = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        Assert.Empty(report.Unreadable);
        var found = Assert.Single(report.Resources, item => item.Kind == ProjectServerResourceKind.DeploymentDirectory);
        Assert.Equal((DeployDir, "portfolio-prod", ProjectServerResourceOutcome.Found), (found.Name, found.Scope, found.Outcome));
        Assert.DoesNotContain(_commands, command => command.StartsWith("delete ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Discover_SaysNothingOfADeploymentDirectory_ThatDoesNotExist()
    {
        await RunAsync(OperationKind.ProjectResourcesDiscover, Identity);

        var report = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        Assert.DoesNotContain(report.Resources, item => item.Kind == ProjectServerResourceKind.DeploymentDirectory);
        Assert.Empty(report.Unreadable);
    }

    [Fact]
    public async Task Remove_DeletesTheConfirmedDeploymentDirectory_AfterTheContainersThatRunFromIt()
    {
        _directories.Add(DeployDir);

        var result = await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [Deployment, Web] });

        Assert.Equal(1, result.ExitCode); // the worker container, the volume... were not confirmed
        var commands = _commands.Where(command => !command.Contains(" ls ", StringComparison.Ordinal) && !command.StartsWith("docker ps", StringComparison.Ordinal)).ToList();
        Assert.Equal(["docker rm -f portfolio-prod-web-1", $"delete {DeployDir}"], commands);
        Assert.DoesNotContain(DeployDir, _directories);
        var report = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        Assert.Equal(ProjectServerResourceOutcome.Removed,
            Assert.Single(report.Resources, item => item.Kind == ProjectServerResourceKind.DeploymentDirectory).Outcome);
    }

    [Fact]
    public async Task Remove_LeavesTheDeploymentDirectory_WhenItWasNotConfirmed()
    {
        _directories.Add(DeployDir);

        await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [Web] });

        Assert.DoesNotContain(_commands, command => command.StartsWith("delete ", StringComparison.Ordinal));
        var left = Assert.Single(ProjectServerResourceOutput.Parse(string.Join('\n', _output)).Resources,
            item => item.Kind == ProjectServerResourceKind.DeploymentDirectory);
        Assert.Equal(ProjectServerResourceOutcome.NotListed, left.Outcome);
    }

    [Theory]
    [InlineData(DeployDir)]
    [InlineData(DeploymentsBase)]
    [InlineData("/var/lib/aetheus-agent")]
    public async Task TheDeploymentDirectory_IsNeverFollowedThroughASymbolicLink(string link)
    {
        _directories.Add(DeployDir);
        _links.Add(link);

        var discovery = await RunAsync(OperationKind.ProjectResourcesDiscover, Identity);
        var discovered = ProjectServerResourceOutput.Parse(string.Join('\n', _output));
        _output.Clear();
        var removal = await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [Deployment] });

        Assert.Equal(0, discovery.ExitCode);
        Assert.DoesNotContain(discovered.Resources, item => item.Kind == ProjectServerResourceKind.DeploymentDirectory);
        Assert.Contains(discovered.Unreadable, item => item.Kind == ProjectServerResourceKind.DeploymentDirectory
            && item.Reason.Contains("is a symbolic link", StringComparison.Ordinal));
        Assert.Equal(1, removal.ExitCode);
        var failed = Assert.Single(ProjectServerResourceOutput.Parse(string.Join('\n', _output)).Resources,
            item => item.Kind == ProjectServerResourceKind.DeploymentDirectory);
        Assert.Equal(ProjectServerResourceOutcome.Failed, failed.Outcome);
        Assert.DoesNotContain(_commands, command => command.StartsWith("delete ", StringComparison.Ordinal));
        Assert.Contains(DeployDir, _directories);
    }

    [Theory]
    [InlineData("/var/lib/aetheus-agent/.deployments/../keys", "portfolio-prod")]
    [InlineData("/var/lib/aetheus-agent/.deployments/portfolio-prod/../../keys", "portfolio-prod")]
    [InlineData("/var/lib/aetheus-agent/.deployments/portfolio-prod/sub", "portfolio-prod")]
    [InlineData("/var/lib/aetheus-agent/.deployments/portfolio-prod/", "portfolio-prod")]
    [InlineData("/var/lib/aetheus-agent/.deployments", "portfolio-prod")]
    [InlineData("/var/lib/aetheus-agent/.deployments/other-prod", "portfolio-prod")]
    [InlineData("/var/lib/aetheus-agent/.deployments/portfolio-prod", "other-prod")]
    [InlineData("/var/lib/aetheus-agent/.deployments/..", "..")]
    [InlineData("/var/lib/aetheus-agent/.deployments/a/b", "a/b")]
    [InlineData("/var/lib/aetheus-agent/.deployments/aetheus-prod", "aetheus-prod")]
    [InlineData("/home/other/.deployments/portfolio-prod", "portfolio-prod")]
    public async Task ACraftedDeploymentDirectory_IsRefusedBeforeTheHostIsTouched(string path, string scope)
    {
        _directories.Add(path);
        var crafted = new ProjectServerResourceRef(ProjectServerResourceKind.DeploymentDirectory, path, scope);

        Assert.False(ProjectServerResourceNames.IsResource(crafted.Kind, crafted.Name, crafted.Scope));
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesRemove, Identity with { Remove = [crafted] })).ExitCode);
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesRemove,
            Identity with { ComposeProjects = [scope], Remove = [crafted] })).ExitCode);
        Assert.Empty(_commands);
        Assert.Contains(path, _directories);
    }

    [Theory]
    [InlineData("../keys")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData(".")]
    public async Task ACraftedComposeProject_CannotReachADeploymentDirectory(string project)
    {
        Assert.Equal(-1, (await RunAsync(OperationKind.ProjectResourcesDiscover, Identity with { ComposeProjects = [project] })).ExitCode);
        Assert.Empty(_commands);
    }

    [Fact]
    public void TheOperations_NeedTheProjectResourcesCapability_ThatTheAgentDeclares()
    {
        Assert.Contains(AgentCapabilities.ProjectResources, AgentCapabilities.SoftwareCapabilities);
        Assert.Equal(AgentCapabilities.ProjectResources, AgentCapabilities.RequiredFor(OperationKind.ProjectResourcesDiscover));
        Assert.Equal(AgentCapabilities.ProjectResources, AgentCapabilities.RequiredFor(OperationKind.ProjectResourcesRemove));
    }
}
