// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Security.Authentication;
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Functions;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Plugins;
using Aetheus.Agent.Core.Toolchains;
using Aetheus.Agent.Core.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Agent.Core.Extensions;

public static class AgentCoreServiceCollectionExtensions
{
    public static IServiceCollection AddAgentCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AetheusAgentOptions>()
            .Bind(configuration.GetSection(AetheusAgentOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AetheusAgentOptions>, AetheusAgentOptionsValidator>();

        services.PostConfigure<AetheusAgentOptions>(opts =>
        {
            if (string.IsNullOrEmpty(opts.WorkDirectory))
            {
                opts.WorkDirectory = OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Aetheus", "Agent")
                    : "/var/lib/aetheus-agent";
            }

            if (string.IsNullOrEmpty(opts.PluginDirectory))
                opts.PluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");

            var storage = opts.DockerStorageMaintenance;
            if (storage.PolicyVersion < DockerStorageMaintenanceOptions.CurrentPolicyVersion)
            {
                storage.PolicyVersion = DockerStorageMaintenanceOptions.CurrentPolicyVersion;
                storage.ReservedSpaceGiB = Math.Min(storage.ReservedSpaceGiB, 5);
                storage.MaxCacheGiB = Math.Min(storage.MaxCacheGiB, 15);
            }
            storage.MaintenanceIntervalMinutes = Math.Clamp(storage.MaintenanceIntervalMinutes, 5, 1440);
            storage.MaxCacheAgeHours = Math.Clamp(storage.MaxCacheAgeHours, 1, 24 * 365);
            storage.PressureCacheAgeHours = Math.Clamp(storage.PressureCacheAgeHours, 1, storage.MaxCacheAgeHours);
            storage.ReservedSpaceGiB = Math.Clamp(storage.ReservedSpaceGiB, 1, 15);
            storage.MaxCacheGiB = Math.Clamp(storage.MaxCacheGiB, storage.ReservedSpaceGiB, 15);
            storage.MinFreeSpaceGiB = Math.Clamp(storage.MinFreeSpaceGiB, 1, 10_000);
            storage.PressureUsedPercent = Math.Clamp(storage.PressureUsedPercent, 1, 99);
            storage.NuGetCacheRetentionDays = Math.Clamp(storage.NuGetCacheRetentionDays, 0, 3650);

            var dangerousPatterns = new[] { ".*", "^.*$", ".+", "^.+$", "^.{0,}$" };
            opts.AllowedCommandPatterns.RemoveAll(p =>
                dangerousPatterns.Contains(p, StringComparer.Ordinal));
        });

        // Resolve options for HTTP client setup
        var options = configuration
            .GetSection(AetheusAgentOptions.SectionName)
            .Get<AetheusAgentOptions>()!;

        ValidateServerUrl(options.ServerUrl);
        AgentTlsPosture.ValidateTlsPosture(options);

        // Core services
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AgentState>();
        services.AddSingleton<AgentRuntimeHealth>();
        services.AddSingleton<ExecutorProcessRunner>();
        services.AddSingleton<IDeploymentBuildRefusalOutbox, DeploymentBuildRefusalOutbox>();
        services.AddSingleton<IAgentProcessRestarter, AgentProcessRestarter>();
        services.AddSingleton<IShellRunner, DefaultShellRunner>();
        services.AddSingleton<DockerStorageMaintenanceService>();
        services.AddSingleton<IDockerStorageMaintenance>(sp => sp.GetRequiredService<DockerStorageMaintenanceService>());
        services.AddTransient<BearerTokenHandler>();
        // Control-plane RPCs (heartbeat, claim, start/complete, log batches): small JSON
        // payloads where a network blip must NOT fail a real build. The standard resilience
        // pipeline owns the timeouts (10s per attempt, 30s total, 3 retries with backoff),
        // so the client timeout is disabled to avoid racing it.
        services.AddHttpClient("AetheusServer", client =>
        {
            client.BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .ConfigurePrimaryHttpMessageHandler(() => BuildPrimaryHandler(options))
        .AddHttpMessageHandler<BearerTokenHandler>()
        .AddStandardResilienceHandler();

        // Large transfers and report ingestion (artifact upload/download, agent archive, analysis
        // reports): a per-attempt timeout would abort large payloads or their normalization, and
        // replaying a partially consumed body is not safe - no resilience here, just a bounded timeout.
        services.AddHttpClient("AetheusServerTransfer", client =>
        {
            client.BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/");
            client.Timeout = AgentRuntimeDefaults.BackendRequestTimeout;
        })
        .ConfigurePrimaryHttpMessageHandler(() => BuildPrimaryHandler(options))
        .AddHttpMessageHandler<BearerTokenHandler>();

        // Immutable public scanner assets. Deliberately has no BearerTokenHandler: following an
        // absolute GitHub release URL with the backend transfer client would disclose the agent token.
        // Integrity is enforced by the manifest SHA-256 before an asset becomes executable. These
        // immutable GETs can be retried safely when transient DNS or registry failures occur.
        services.AddHttpClient("AetheusScannerDownload", client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            UseCookies = false
        })
        .AddStandardResilienceHandler();

        // AI provider client (ADR-023, amendment 2026-10-11): the HTTPS APIs a runner profile names.
        // No redirect, because the request carries the API key in a header a redirect would hand to
        // another host; no resilience handler, because AiProviderClient owns the retries and bounds them
        // by the task's timeout. The system proxy applies, as for any outbound call of the agent.
        services.AddHttpClient(AiRunOperationExecutor.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        });

        // App-availability probe client (ADR-021): plain outbound calls to localhost/LAN apps, zero
        // elevation, no auth token, no redirects. TLS validation is relaxed because a probed app may use
        // a self-signed cert and this channel authenticates nothing - it only checks reachability/status.
        services.AddHttpClient(AppProbeWorker.ProbeHttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan; // per-probe timeout is enforced via a linked CTS
        })
        .ConfigurePrimaryHttpMessageHandler(BuildAppProbeHandler);

        // Deployment probe client, shared by `type: smoke` and the blue-green readiness gate. Both aim
        // at an origin the deployment itself controls, so redirects and proxies would only obscure what
        // that origin actually answered.
        services.AddHttpClient(SmokeOperationExecutor.DeploymentProbeHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        });

        services.AddSingleton<IServerApiClient, ServerApiClient>();
        services.AddSingleton<EnrollmentService>();
        services.AddSingleton<IEnrollmentService>(sp => sp.GetRequiredService<EnrollmentService>());
        services.AddSingleton<ICommandValidator, CommandValidator>();
        services.AddSingleton<DockerCollector>();
        services.AddSingleton<IDockerCollector>(sp => sp.GetRequiredService<DockerCollector>());
        services.AddSingleton<ApacheCollector>();
        services.AddSingleton<IApacheCollector>(sp => sp.GetRequiredService<ApacheCollector>());
        services.AddSingleton<CertbotCollector>();
        services.AddSingleton<ICertbotCollector>(sp => sp.GetRequiredService<CertbotCollector>());
        services.AddSingleton<MailCollector>();
        services.AddSingleton<IMailCollector>(sp => sp.GetRequiredService<MailCollector>());
        services.AddSingleton<ITeamspeakQueryTransport, TcpTeamspeakQueryTransport>();
        services.AddSingleton<ITeamspeakQueryClient, TcpTeamspeakQueryClient>();
        services.AddSingleton<TeamspeakCollector>();
        services.AddSingleton<ITeamspeakCollector>(sp => sp.GetRequiredService<TeamspeakCollector>());
        services.AddSingleton<PortsentryCollector>();
        services.AddSingleton<IPortsentryCollector>(sp => sp.GetRequiredService<PortsentryCollector>());
        services.AddSingleton<ListeningPortsCollector>();
        services.AddSingleton<IListeningPortsCollector>(sp => sp.GetRequiredService<ListeningPortsCollector>());
        services.AddSingleton<RkhunterCollector>();
        services.AddSingleton<IRkhunterCollector>(sp => sp.GetRequiredService<RkhunterCollector>());
        services.AddSingleton<SecurityUpdatesCollector>();
        services.AddSingleton<ISecurityUpdatesCollector>(sp => sp.GetRequiredService<SecurityUpdatesCollector>());
        services.AddSingleton<FirewallCollector>();
        services.AddSingleton<IFirewallCollector>(sp => sp.GetRequiredService<FirewallCollector>());
        services.AddSingleton<ISudoersHashCollector, SudoersHashCollector>();
        services.AddSingleton<Collectors.Ssh.ISshConfigCollector, Collectors.Ssh.SshConfigCollector>();

        // Docker executor (cross-platform)
        services.AddSingleton<IExecutor, DockerExecutor>();

        // Phase 2: ephemeral hardened container executor for isolation: container pipeline steps.
        services.AddSingleton<IToolchainResolver, ToolchainResolver>();
        services.AddSingleton<IContainerExecutor, ContainerRunExecutor>();

        // F-32: typed operation executors. These run platform commands via fixed argument lists
        // - no shell, no allow-list regex, target string validated per kind.
        services.AddSingleton<IOperationExecutor, DockerOperationExecutor>();
        services.AddSingleton<IOperationExecutor, DockerPayloadOperationExecutor>();
        services.AddSingleton<IOperationExecutor, ServiceOperationExecutor>();
        services.AddSingleton<IOperationExecutor, PackageOperationExecutor>();
        services.AddSingleton<IOperationExecutor, SystemPackageUpgradeExecutor>();
        services.AddSingleton<IOperationExecutor, FirewallOperationExecutor>();
        services.AddSingleton<IOperationExecutor, PortsObserveOperationExecutor>();
        services.AddSingleton<IOperationExecutor, BackupOperationExecutor>();
        services.AddSingleton<IOperationExecutor, ApacheOperationExecutor>();
        services.AddSingleton<IOperationExecutor, CertbotOperationExecutor>();
        services.AddSingleton<IOperationExecutor, RkhunterOperationExecutor>();
        services.AddSingleton<IOperationExecutor, AgentSelfUpdateOperationExecutor>();
        services.AddSingleton<IOperationExecutor, MailOperationExecutor>();
        // The outbound port 25 prerequisite, run by the agent itself (no helper).
        services.AddSingleton<IOutboundTcpConnector, OutboundTcpConnector>();
        services.AddSingleton<IOperationExecutor, MailOutboundProbeOperationExecutor>();
        // Recette R3-23: what a mail stack left on the host, read by the agent itself (no helper).
        services.AddSingleton<IOperationExecutor, MailLeftoversOperationExecutor>();
        // Recette R3-59: the read-only list of installed packages, read by the agent itself (no sudo).
        services.AddSingleton<IOperationExecutor, PackageInventoryOperationExecutor>();
        services.AddSingleton<IOperationExecutor, PortsentryOperationExecutor>();
        services.AddSingleton<IOperationExecutor, CronOperationExecutor>();
        services.AddSingleton<IOperationExecutor, PipelineArtifactOperationExecutor>();
        services.AddSingleton<IFileSystemReader, PhysicalFileSystemReader>();
        services.AddSingleton<IOperationExecutor, PipelineDotnetTestOperationExecutor>();
        services.AddSingleton<IOperationExecutor, PipelineGateStatusOperationExecutor>();
        // type: aetheus steps. Function implementations register as IPipelineFunction; a catalog entry
        // without one fails as function-not-implemented. The set is PipelineFunctions, the one the
        // aetheus-fn executable runs inside step containers, so both paths implement the same functions.
        foreach (var function in PipelineFunctions.CreateAll())
            services.AddSingleton(function);
        services.AddSingleton<IOperationExecutor, PipelineFunctionOperationExecutor>();
        services.AddSingleton<ContainerPipelineFunctionExecutor>();
        services.AddSingleton<IScannerProcessRunner, ScannerProcessRunner>();
        services.AddSingleton<ScannerSourceProjectionManager>();
        services.AddSingleton<IOperationExecutor, ScannerOperationExecutor>();
        services.AddSingleton<IOperationExecutor, AnalysisGateOperationExecutor>();
        services.AddSingleton<IOperationExecutor, ObservabilityPackageOperationExecutor>();
        services.AddSingleton<IOperationExecutor, AiRunOperationExecutor>();
        services.AddSingleton<IOperationExecutor, TeamspeakServerQueryOperationExecutor>();
        services.AddSingleton<IOperationExecutor, TeamspeakSetupOperationExecutor>();
        services.AddSingleton<IOperationExecutor, TeamspeakGracefulRestartOperationExecutor>();
        services.AddSingleton<IOperationExecutor, DeployOperationExecutor>();
        services.AddSingleton<IOperationExecutor, SmokeOperationExecutor>();
        services.AddSingleton<IOperationExecutor, BlueGreenOperationExecutor>();

        // Plugins
        PluginLoader.AddPluginLoader(services);

        // Background services
        services.AddHostedService<AgentStartupService>();
        services.AddHostedService<HeartbeatService>();
        services.AddHostedService<PollingService>();
        services.AddHostedService<AgentLivenessWatchdogService>();
        services.AddHostedService<AppProbeWorker>();
        services.AddHostedService<RunWorkspaceReaper>();
        services.AddHostedService<Aetheus.Agent.Core.Operations.BlueGreenConfirmationWatchdog>();
        services.AddHostedService(sp => sp.GetRequiredService<DockerStorageMaintenanceService>());

        return services;
    }

    // Extracted from AddAgentCore so the suppression below covers this handler alone. Left inline,
    // it would have had to sit on the whole registration method and would then silently cover any
    // future client added there.
    [SuppressMessage("Security", "CA5359:Do not disable certificate validation",
        Justification = "ADR-021 probe channel: authenticates nothing, carries no token, and only reports whether a possibly self-signed app answers.")]
    [SuppressMessage("Aetheus.Security", "SEC008",
        Justification = "Same reason as CA5359 above: the ADR-021 probe channel authenticates nothing and carries no token.")]
    private static SocketsHttpHandler BuildAppProbeHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        SslOptions = new SslClientAuthenticationOptions
        {
            // nosemgrep: aetheus.csharp.certificate-validation-always-true - ADR-021 probe channel, authenticates nothing and carries no token (see SEC008 above).
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        }
    };

    // CA5398 would have this pass SslProtocols.None and let the OS pick. Pinning 1.2 and 1.3 is the
    // stricter choice, not the laxer one: the agent runs on hosts we do not control, and the system
    // default still negotiates older versions on some of them. Revisit when 1.3 alone is realistic.
    [SuppressMessage("Security", "CA5398:Avoid hardcoded SslProtocols values",
        Justification = "Pins TLS 1.2/1.3 deliberately; the OS default is weaker on some agent hosts.")]
    private static SocketsHttpHandler BuildPrimaryHandler(AetheusAgentOptions options) => new()
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = AgentTlsPosture.BuildCertValidationCallback(options)
        }
    };

    private static void ValidateServerUrl(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Invalid ServerUrl: {serverUrl}");

        var isLoopback = uri.Host is "localhost" or "127.0.0.1" or "::1";
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) && !isLoopback)
            throw new InvalidOperationException("ServerUrl must use HTTPS for non-loopback addresses.");
    }
}
