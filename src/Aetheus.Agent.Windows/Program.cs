// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Extensions;
using Aetheus.Agent.Core.Services;
using Aetheus.Agent.Core.Workers;
using Aetheus.Agent.Windows.Collectors;
using Aetheus.Agent.Windows.Executors;
using Aetheus.Agent.Windows.Services;

// CLI mode: one-shot probe used by the installer right after writing
// appsettings.json (parity with Aetheus.Agent.Linux). Runs the same .NET
// HTTP stack the agent will use, so TLS / DNS / proxy issues surface here
// instead of inside a service start-timeout. Exit code feeds the installer.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
var probeExitCode = await AgentBackendProbeEntrypoint.TryRunAsync(args, builder.Configuration).ConfigureAwait(false);
if (probeExitCode.HasValue)
    return probeExitCode.Value;

// Register shared agent services (config, enrollment, polling, heartbeat, docker)
builder.Services.AddAgentCore(builder.Configuration);

// Windows-specific implementations
builder.Services.AddSingleton<ICredentialProtector, WindowsCredentialProtector>();
builder.Services.AddSingleton<IMetricsCollector, WindowsMetricsCollector>();
builder.Services.AddSingleton<IServiceCollector, WindowsServiceCollector>();
builder.Services.AddSingleton<IExecutor, WindowsShellExecutor>();

// Windows Service support
builder.Services.AddWindowsService();

var host = builder.Build();
host.Run();
return 0;
