// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Keeps the Testcontainers containers of one test step identifiable and removable without Ryuk.
/// Ryuk, the Testcontainers reaper, runs privileged with the Docker socket mounted and under a name that
/// changes every run, so it raises a Critical container-risk alert on every CI run. The step disables it
/// (<c>TESTCONTAINERS_RYUK_DISABLED</c>, honoured by Testcontainers .NET) and this scope replaces its job:
/// <list type="bullet">
/// <item>the test process gets <see cref="RunVariable"/> and <see cref="StartedAtVariable"/>, which the
/// integration tests copy onto their containers as the <see cref="RunLabel"/> and
/// <see cref="StartedAtLabel"/> labels;</item>
/// <item>when the step ends (success, failure or cancellation) the containers carrying this run's label are
/// removed;</item>
/// <item>before the tests start, the labelled containers whose start is older than
/// <see cref="StaleAfter"/> (longer than any step may run, so their run is over) are removed, which cleans
/// up after a run whose agent was killed. A container of a run that may still be running is never
/// touched.</item>
/// </list>
/// A Docker failure is logged and tolerated: cleanup must not fail a graded test run.
/// </summary>
internal sealed class TestcontainersRunScope
{
    internal const string RunVariable = "AETHEUS_TESTCONTAINERS_RUN";
    internal const string StartedAtVariable = "AETHEUS_TESTCONTAINERS_STARTED_AT";
    internal const string RunLabel = "aetheus.testcontainers.run";
    internal const string StartedAtLabel = "aetheus.testcontainers.started-at";
    internal const string RyukDisabledVariable = "TESTCONTAINERS_RYUK_DISABLED";

    /// <summary>Above the longest step timeout (2700 s) of a test step.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    private readonly FunctionDocker _docker;
    private readonly PipelineFunctionContext _context;
    private readonly string _run = Guid.NewGuid().ToString("N");
    private readonly TimeProvider _time;
    private readonly long _startedAt;

    public TestcontainersRunScope(IPipelineFunctionTools tools, PipelineFunctionContext context, TimeProvider? time = null)
    {
        _context = context;
        _time = time ?? TimeProvider.System;
        _startedAt = _time.GetUtcNow().ToUnixTimeSeconds();
        _docker = new FunctionDocker(tools, context);
    }

    /// <summary>The environment of the test process.</summary>
    public IReadOnlyDictionary<string, string> Variables => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [RyukDisabledVariable] = "true",
        [RunVariable] = _run,
        [StartedAtVariable] = _startedAt.ToString(CultureInfo.InvariantCulture)
    };

    public async Task RemoveStaleAsync(CancellationToken cancellationToken)
    {
        if (!_docker.IsAvailable)
            return;
        var rows = await _docker.LinesAsync(cancellationToken, "ps", "-a",
            "--filter", "label=org.testcontainers=true", "--filter", "label=" + StartedAtLabel,
            "--format", "{{.ID}} {{.Label \"" + StartedAtLabel + "\"}}").ConfigureAwait(false);
        if (rows is null)
        {
            await _context.Log("Testcontainers: the stale containers could not be listed; skipped.").ConfigureAwait(false);
            return;
        }

        var limit = _time.GetUtcNow().Subtract(StaleAfter).ToUnixTimeSeconds();
        var stale = rows
            .Select(row => row.Split(' ', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var started)
                && started < limit)
            .Select(parts => parts[0])
            .ToArray();
        await RemoveAsync(stale, "stale", cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveOwnAsync()
    {
        if (!_docker.IsAvailable)
            return;
        var ids = await _docker.LinesAsync(CancellationToken.None, "ps", "-aq",
            "--filter", "label=org.testcontainers=true", "--filter", $"label={RunLabel}={_run}").ConfigureAwait(false);
        if (ids is null)
        {
            await _context.Log("Testcontainers: this run's containers could not be listed; skipped.").ConfigureAwait(false);
            return;
        }

        await RemoveAsync(ids, "this run's", CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RemoveAsync(IReadOnlyCollection<string> ids, string kind, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return;
        var exit = await _docker.QuietAsync(cancellationToken, ["rm", "-f", "-v", .. ids]).ConfigureAwait(false);
        await _context.Log(string.Create(CultureInfo.InvariantCulture,
            $"Testcontainers: removed {ids.Count} {kind} container(s) (exit {exit}).")).ConfigureAwait(false);
    }
}
