// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Runs the expand/contract gate of the blue-green migrate step and publishes its verdict.
///
/// Both colours share one schema, and the previous one is still answering requests while the new one
/// migrates, so a pending migration that drops or renames anything would break production
/// mid-deployment. <see cref="BlueGreenMigrationGate"/> holds the rules; this class reads the inputs,
/// writes the human log lines, and ends every path with one <see cref="MigrationGateReport.Directive"/>
/// line the control plane keeps against the run (recette R1-43), so the verdict outlives the step log.
/// </summary>
internal sealed class BlueGreenMigrationGateCheck(IShellRunner shell)
{
    /// <summary>
    /// Null when the migration bundle may run; otherwise the result the step must end with.
    /// </summary>
    internal async Task<ExecutorResult?> RunAsync(
        BlueGreenContext context, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var migrationsDirectory = envVars.GetValueOrDefault("AETHEUS_BG_MIGRATIONS_DIR", string.Empty).Trim();
        if (migrationsDirectory.Length == 0)
        {
            const string missing = "AETHEUS_BG_MIGRATIONS_DIR is required: the expand/contract gate cannot run without the migration sources.";
            await onOutput(missing, TaskLogLevel.Error).ConfigureAwait(false);
            await PublishAsync(Unavailable(missing), onOutput).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        // An unreadable directory yields no discovered migrations, which would otherwise read as
        // "nothing pending" and wave the bundle through with the gate silently disabled. A gate that
        // cannot see its inputs has to fail, not report success.
        if (!Directory.Exists(migrationsDirectory))
        {
            var notFound = $"Migration sources not found at '{migrationsDirectory}'; the expand/contract gate cannot inspect what is about to run. "
                + "Provide an absolute path, or one that exists for the agent.";
            await onOutput(notFound, TaskLogLevel.Error).ConfigureAwait(false);
            await PublishAsync(Unavailable(notFound), onOutput).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var (historyRead, applied) = await BlueGreenSchemaHistory
            .ReadAsync(shell, context, timeoutSeconds, onOutput, ct).ConfigureAwait(false);
        if (!historyRead)
        {
            await PublishAsync(
                Unavailable("The schema history of the shared database could not be read."), onOutput).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var discovered = BlueGreenMigrationGate.DiscoverMigrationIds(migrationsDirectory);
        var pending = BlueGreenMigrationGate.Pending(discovered, applied);
        if (pending.Count == 0)
        {
            await onOutput("No pending EF migration; the schema is already current.", TaskLogLevel.Info).ConfigureAwait(false);
            await PublishAsync(new MigrationGateReport { Outcome = MigrationGateOutcome.NoPending }, onOutput).ConfigureAwait(false);
            return null;
        }
        if (applied.Count == 0)
        {
            // An empty history is an initial schema creation: there is no live colour serving the
            // old shape, so the compatibility contract has nothing to protect yet.
            await onOutput(
                $"Initial schema creation ({pending.Count} migration(s)); no live colour to stay compatible with.",
                TaskLogLevel.Info).ConfigureAwait(false);
            await PublishAsync(
                new MigrationGateReport { Outcome = MigrationGateOutcome.InitialCreation, PendingCount = pending.Count },
                onOutput).ConfigureAwait(false);
            return null;
        }

        var verdicts = new List<MigrationGateMigrationVerdict>();
        var violations = new List<string>();
        foreach (var migrationId in pending)
        {
            var path = Path.Combine(migrationsDirectory, migrationId + ".cs");
            IReadOnlyList<string> found = File.Exists(path)
                ? BlueGreenMigrationGate.Inspect(migrationId, await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))
                : [$"{migrationId}: pending migration source is missing at {path}."];
            violations.AddRange(found);
            verdicts.Add(new MigrationGateMigrationVerdict
            {
                MigrationId = migrationId,
                Compatible = found.Count == 0,
                Violations = [.. found.Take(MigrationGateReport.MaxViolationsPerMigration).Select(Bound)]
            });
        }
        var report = new MigrationGateReport
        {
            Outcome = violations.Count > 0 ? MigrationGateOutcome.Refused : MigrationGateOutcome.Compatible,
            PendingCount = pending.Count,
            Migrations = [.. verdicts.Take(MigrationGateReport.MaxMigrations)]
        };
        if (violations.Count > 0)
        {
            await onOutput(
                "Pending migrations are not expand-compatible. Ship expand and contract in separate releases:",
                TaskLogLevel.Error).ConfigureAwait(false);
            foreach (var violation in violations)
                await onOutput(violation, TaskLogLevel.Error).ConfigureAwait(false);
            await PublishAsync(report, onOutput).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }
        await onOutput($"All {pending.Count} pending migration(s) are expand-compatible.", TaskLogLevel.Info)
            .ConfigureAwait(false);
        await PublishAsync(report, onOutput).ConfigureAwait(false);
        return null;
    }

    /// <summary>The directive line for one report: the shared prefix, then the report in JSON.</summary>
    internal static string DirectiveLine(MigrationGateReport report) =>
        MigrationGateReport.Directive + JsonSerializer.Serialize(report, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static MigrationGateReport Unavailable(string detail) =>
        new() { Outcome = MigrationGateOutcome.Unavailable, Detail = Bound(detail) };

    private static string Bound(string text) =>
        text.Length <= MigrationGateReport.MaxTextLength ? text : text[..MigrationGateReport.MaxTextLength];

    private static Task PublishAsync(MigrationGateReport report, Func<string, TaskLogLevel, Task> onOutput) =>
        onOutput(DirectiveLine(report), TaskLogLevel.Info);
}
