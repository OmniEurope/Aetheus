// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Plugins;

namespace Aetheus.Agent.Core.Workers;

public sealed class AgentStartupService(
    IEnrollmentService enrollment,
    IPluginLoader pluginLoader,
    IOptions<AetheusAgentOptions> options,
    TimeProvider timeProvider,
    ILogger<AgentStartupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workDir = options.Value.WorkDirectory;
        if (!string.IsNullOrEmpty(workDir))
        {
            Directory.CreateDirectory(workDir);
            AgentUpdateRecoveryState.RegisterStartedProcess(workDir);
        }

        await pluginLoader.LoadPluginsAsync(options.Value.PluginDirectory, stoppingToken).ConfigureAwait(false);

        await enrollment.LoadPersistedCredentialsAsync(stoppingToken).ConfigureAwait(false);

        if (enrollment.IsEnrolled)
        {
            logger.LogInformation("Agent is enrolled and ready");
            return;
        }

        logger.LogInformation("Agent not enrolled, attempting enrollment...");
        var maxRetries = 10;
        var delay = TimeSpan.FromSeconds(5);

        for (var i = 0; i < maxRetries && !stoppingToken.IsCancellationRequested; i++)
        {
            // Wrap every attempt in try/catch so an unexpected exception (network issue,
            // TLS error, etc.) does NOT crash the host. BackgroundServiceExceptionBehavior
            // defaults to StopHost and would cause a systemd restart loop, masking the
            // real cause behind a growing restart counter.
            try
            {
                if (await enrollment.EnrollAsync(stoppingToken).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Enrollment attempt {Attempt}/{Max} threw unexpectedly", i + 1, maxRetries);
            }

            logger.LogWarning("Enrollment attempt {Attempt}/{Max} failed, retrying in {Delay}s",
                i + 1, maxRetries, delay.TotalSeconds);
            await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }

        logger.LogError("Enrollment failed after {Max} attempts. Agent will not function until enrolled", maxRetries);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await pluginLoader.UnloadAllAsync(cancellationToken).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
