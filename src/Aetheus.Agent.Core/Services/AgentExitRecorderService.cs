// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Services;

/// <summary>
/// Writes the exit record for the two endings the agent does not choose itself: a stop requested by the
/// host (SIGTERM, service stop) and an unhandled exception. A deliberate restart records its own reason
/// through <see cref="IAgentProcessRestarter"/> first, and the journal keeps only the first record.
/// </summary>
internal sealed class AgentExitRecorderService(
    IAgentExitJournal exitJournal,
    IHostApplicationLifetime lifetime) : IHostedService, IDisposable
{
    private CancellationTokenRegistration _stopping;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Loads the previous process's record and marks this one as running, before anything can fail.
        _ = exitJournal.GetPendingReport();
        _stopping = lifetime.ApplicationStopping.Register(() => exitJournal.RecordExit(
            AgentExitKinds.Stopped, "The host asked the agent process to stop (SIGTERM or service stop)."));
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        _stopping.Dispose();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args) =>
        exitJournal.RecordExit(AgentExitKinds.Crash, DescribeCrash(args.ExceptionObject));

    internal static string DescribeCrash(object exceptionObject) =>
        exceptionObject is Exception exception
            ? $"Unhandled {exception.GetType().Name}: {exception.Message}"
            : $"Unhandled non-exception object: {exceptionObject}";
}
