// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The containers of one <c>run-qa-e2e-suite</c> call, in the script's order: the test network
/// resolved from the front container, the readiness wait, the suite run (with progress lines), the
/// results copied out of the stopped suite container, and the classification of the raw exit code it
/// recorded against its TRX. Removing the containers is the caller's, on every path.
/// </summary>
internal sealed class E2eExecution(
    IPipelineFunctionTools tools, PipelineFunctionContext context, FunctionDocker docker, E2eRun run, TimeSpan interval)
{
    private const string NetworksTemplate = "{{range $k,$_ := .NetworkSettings.Networks}}{{$k}}{{\"\\n\"}}{{end}}";

    /// <summary>The findings status, or the reason no gradable evidence exists.</summary>
    public async Task<(int Status, string? Error)> RunAsync(CancellationToken cancellationToken)
    {
        var network = await NetworkAsync(cancellationToken).ConfigureAwait(false);
        if (network is null)
        {
            await context.Log($"Could not resolve the QA network for {run.Front}.", TaskLogLevel.Error).ConfigureAwait(false);
            await docker.RunAsync(cancellationToken, "ps").ConfigureAwait(false);
            return (0, $"Could not resolve the QA network for {run.Front}.");
        }
        if (await docker.RunAsync(cancellationToken, run.ReadyArguments(network)).ConfigureAwait(false) is not 0 and var notReady)
            return (0, string.Create(CultureInfo.InvariantCulture, $"The front {run.Front} did not answer on {network} (readiness exit code {notReady})."));

        var password = (await File.ReadAllTextAsync(run.PasswordFile, cancellationToken).ConfigureAwait(false)).TrimEnd('\n');
        var suite = FunctionShell.StartInfo(context, FunctionShell.Tool(context, "docker"), context.Workspace!, run.RunnerArguments(network, password));
        var suiteExit = await FunctionProgress
            .RunAsync(tools, context, $"Playwright E2E tests ({run.Label})", interval, suite, cancellationToken)
            .ConfigureAwait(false);
        if (suiteExit != 0)
            return (0, string.Create(CultureInfo.InvariantCulture, $"The E2E suite container {run.RunnerContainer} failed with exit code {suiteExit}."));
        // The browser process stays non-root without any host/container UID mapping; the daemon
        // materializes the completed evidence only after the runner container has stopped.
        var copied = await docker.RunAsync(cancellationToken, "cp", $"{run.RunnerContainer}:{E2eRun.ContainerResults}/.", run.Results + "/")
            .ConfigureAwait(false);
        if (copied != 0)
            return (0, string.Create(CultureInfo.InvariantCulture, $"docker cp of the E2E results failed with exit code {copied}."));
        return await ClassifyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The first network of the front container that is not the backend one, or null.</summary>
    private async Task<string?> NetworkAsync(CancellationToken cancellationToken)
    {
        var networks = await docker.LinesAsync(cancellationToken, "inspect", "-f", NetworksTemplate, run.Front).ConfigureAwait(false);
        return networks?.FirstOrDefault(name => !name.EndsWith("_backend", StringComparison.Ordinal));
    }

    private async Task<(int Status, string? Error)> ClassifyAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(run.RawExitFile) || new FileInfo(run.RawExitFile).Length == 0)
            return (0, $"The E2E suite recorded no raw exit code in {run.RawExitFile}.");
        var raw = (await File.ReadAllTextAsync(run.RawExitFile, cancellationToken).ConfigureAwait(false))
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        var rawExitCode = int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
        var verdict = DotnetTestResultClassifier.Classify(rawExitCode, run.Trx, null, new PhysicalFileSystemReader());
        return verdict.IsValue
            ? (verdict.Value, null)
            : (0, $"The E2E test evidence cannot be graded: {verdict.Failure.Reason}");
    }
}
