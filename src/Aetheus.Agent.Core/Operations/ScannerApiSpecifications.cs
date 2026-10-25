// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aetheus.Agent.Core.Operations;

/// <summary> One <c>zap-api</c> step may name several API definitions, scanned one after the other
/// on this agent (M-021: no parallelism here) and published as one report. The partitions exist to
/// bound each scan, not to save time, so each keeps its own share of the step's timeout. </summary>
internal static class ScannerApiSpecifications
{
    internal const string ListVariable = "AETHEUS_SCANNER_API_SPECIFICATION_URLS";
    internal const string SingleVariable = "AETHEUS_SCANNER_API_SPECIFICATION_URL";

    /// <summary>The definitions of the step: the list when one is given, else the single URL, else none.</summary>
    internal static IReadOnlyList<string> Read(IReadOnlyDictionary<string, string> envVars)
    {
        var list = (envVars.GetValueOrDefault(ListVariable) ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (list.Length > 0) return list;
        return envVars.GetValueOrDefault(SingleVariable) is { Length: > 0 } single ? [single] : [];
    }

    /// <summary>True when the step names a list: it is scanned partition by partition.</summary>
    internal static bool IsPartitioned(IReadOnlyDictionary<string, string> envVars) =>
        !string.IsNullOrWhiteSpace(envVars.GetValueOrDefault(ListVariable));

    /// <summary>The environment of one partition: the same step, with that definition as its single URL.</summary>
    internal static IReadOnlyDictionary<string, string> ForPartition(
        IReadOnlyDictionary<string, string> envVars, string specificationUrl) =>
        new Dictionary<string, string>(envVars, StringComparer.OrdinalIgnoreCase)
        {
            [SingleVariable] = specificationUrl,
            [ListVariable] = string.Empty
        };

    /// <summary>Each partition's bound: the step's timeout shared equally, one second at least.</summary>
    internal static int PartitionTimeoutSeconds(int stepTimeoutSeconds, int partitions) =>
        Math.Max(1, stepTimeoutSeconds / Math.Max(1, partitions));

    /// <summary>
    /// Scans every definition of the step in order, each in its own output directory with its share of
    /// the timeout, stops at the first partition that fails (its failure is the step's), and merges the
    /// reports into <paramref name="reportPath"/>. The step's log names each partition and its alerts.
    /// The exit code returned is the highest of the partitions: findings in any part are findings.
    /// </summary>
    internal static async Task<(ExecutorResult? Failure, int ExitCode)> ScanInSequenceAsync(
        ScanInvocation invocation,
        IReadOnlyDictionary<string, string> environment,
        string reportPath,
        Func<ScanInvocation, IReadOnlyDictionary<string, string>, string, string, Task<(ExecutorResult? Failure, int ExitCode)>> scanPartition)
    {
        var specifications = Read(environment);
        var timeoutSeconds = PartitionTimeoutSeconds(invocation.TimeoutSeconds, specifications.Count);
        var reports = new List<string>(specifications.Count);
        var exitCode = 0;
        for (var index = 0; index < specifications.Count; index++)
        {
            var scope = $"OpenAPI partition {index + 1}/{specifications.Count}: ";
            var partition = invocation with
            {
                OutputDirectory = Path.Combine(invocation.ScanRoot, $"partition-{index}"),
                TimeoutSeconds = timeoutSeconds
            };
            Directory.CreateDirectory(partition.OutputDirectory);
            ScannerFileSecurity.TrySetOwnerOnly(partition.OutputDirectory);
            var partitionReport = Path.Combine(partition.OutputDirectory, invocation.Scanner.ReportPath);
            await invocation.OnOutput(
                $"{scope}scanning {specifications[index]} (at most {timeoutSeconds} s).", TaskLogLevel.Info)
                .ConfigureAwait(false);
            var run = await scanPartition(
                partition, ForPartition(environment, specifications[index]), partitionReport, scope).ConfigureAwait(false);
            if (run.Failure is not null) return run;
            reports.Add(partitionReport);
            exitCode = Math.Max(exitCode, run.ExitCode);
        }
        var alerts = await MergeZapReportsAsync(reports, reportPath, invocation.CancellationToken).ConfigureAwait(false);
        await invocation.OnOutput(
            "One report for the " + specifications.Count + " partitions, alerts per partition: "
            + string.Join(", ", alerts.Select((count, index) => $"{index + 1}={count}")) + ".",
            TaskLogLevel.Info).ConfigureAwait(false);
        return (null, exitCode);
    }

    /// <summary>
    /// Writes one ZAP JSON report holding every site of the partition reports, in partition order, and
    /// returns how many alerts each partition contributed. The header (program, version, date) is the
    /// first partition's. Alerts are not deduplicated here: the backend fingerprints findings, exactly
    /// as it did when each partition published its own report.
    /// </summary>
    internal static async Task<IReadOnlyList<int>> MergeZapReportsAsync(
        IReadOnlyList<string> partitionReports, string destination, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfZero(partitionReports.Count);
        JsonObject? merged = null;
        var sites = new JsonArray();
        var alertsPerPartition = new List<int>(partitionReports.Count);
        foreach (var path in partitionReports)
        {
            await using var stream = File.OpenRead(path);
            var root = await JsonNode.ParseAsync(stream, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }, cancellationToken: ct)
                .ConfigureAwait(false) as JsonObject
                ?? throw new InvalidDataException($"ZAP report '{Path.GetFileName(path)}' is not a JSON object.");
            var alerts = 0;
            if (root["site"] is JsonArray partitionSites)
            {
                foreach (var site in partitionSites)
                {
                    alerts += site?["alerts"] is JsonArray siteAlerts ? siteAlerts.Count : 0;
                    sites.Add(site?.DeepClone());
                }
            }
            alertsPerPartition.Add(alerts);
            merged ??= root;
        }
        merged!["site"] = sites;
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await JsonSerializer.SerializeAsync(output, merged, cancellationToken: ct).ConfigureAwait(false);
        return alertsPerPartition;
    }
}
