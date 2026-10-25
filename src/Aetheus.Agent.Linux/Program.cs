// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Extensions;
using Aetheus.Agent.Core.Services;
using Aetheus.Agent.Core.Workers;
using Aetheus.Agent.Linux.Collectors;
using Aetheus.Agent.Linux.Executors;
using Aetheus.Agent.Linux.Services;

// CLI mode: one-shot probe used by the installer right after writing
// appsettings.json. Runs the same .NET HTTP stack the agent will use, so
// TLS / DNS / proxy issues invisible to curl surface here instead of inside
// a 2-minute systemd start-timeout. Exit code feeds the installer.
var builder = Host.CreateApplicationBuilder(args);
var probeExitCode = await AgentBackendProbeEntrypoint.TryRunAsync(args, builder.Configuration).ConfigureAwait(false);
if (probeExitCode.HasValue)
    return probeExitCode.Value;

// Register shared agent services (config, enrollment, polling, heartbeat, docker)
builder.Services.AddAgentCore(builder.Configuration);

// Linux-specific implementations
builder.Services.AddSingleton<ICredentialProtector, LinuxCredentialProtector>();
builder.Services.AddSingleton<IMetricsCollector, LinuxMetricsCollector>();
builder.Services.AddSingleton<IServiceCollector, LinuxServiceCollector>();
builder.Services.AddSingleton<IExecutor, LinuxShellExecutor>();

// systemd support
builder.Services.AddSystemd();

var host = builder.Build();
host.Run();
return 0;
