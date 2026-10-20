// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>record-storage-evidence</c> function, port of <c>deploy/scripts/record-storage-evidence.sh</c>:
/// at the end of a successful QA, records what the host's storage looks like (the Buildx builder's
/// cache, the root filesystem) in a ledger kept in the agent's work directory, refuses to record while a
/// QA Compose stack of this run or an older one survives, and once five samples of the builder exist,
/// verifies that the cache has reached a plateau within its budget.
/// <list type="bullet">
/// <item>Inputs, checked in the script's order and with its messages: <c>scope</c> and <c>run_id</c>
/// (<c>[a-zA-Z0-9_.-]</c> only), the two thresholds in GiB (non-negative integers), the builder, and
/// <c>AETHEUS_AGENT_WORK_DIRECTORY</c>, which must exist so the ledger survives workspace cleanup.</item>
/// <item>Measures: <c>docker buildx du --builder</c> (its <c>Total</c> and <c>Reclaimable</c> lines,
/// decimal and binary units, as bytes), <c>df -Pk /</c> (used percent, free bytes), and the Compose
/// containers and volumes of the host.</item>
/// <item>A residual is a resource whose Compose project is <c>&lt;slug&gt;-qa-&lt;n&gt;</c> or
/// <c>&lt;slug&gt;-qa-rollback-&lt;n&gt;</c> with <c>n</c> not above this run: a newer run's stack is
/// another QA still running on the host and is only reported.</item>
/// <item>Ledger <c>&lt;work&gt;/storage-evidence/&lt;scope&gt;-v2.tsv</c>: this run's row for this builder
/// replaced, the last 50 rows kept, mode 600. The plateau holds when none of the builder's last five
/// samples exceeds the cache budget or saw a residual, and the cache of the last three varies by no more
/// than the allowed growth.</item>
/// </list>
///
/// Differences, deliberate: the script's arguments and variables are parameters defaulting to them
/// (<c>scope</c> to <c>qa</c>, <c>run_id</c> to <c>BUILD_BUILDID</c>, <c>builder</c> to
/// <c>AETHEUS_BUILDX_BUILDER</c> then <c>BUILDX_BUILDER</c>, the thresholds to
/// <c>AETHEUS_STORAGE_MAX_CACHE_GIB</c> (80) and <c>AETHEUS_STORAGE_PLATEAU_GROWTH_GIB</c> (5)); the QA
/// stacks are named after the project (<c>project_name</c>, default <c>BUILD_PROJECTNAME</c>, slugged as
/// <c>qa-stack-identity.sh</c> does) where the script matched <c>aetheus-qa-</c> only. A <c>df</c> that
/// fails or prints nothing usable fails the function, where the script recorded empty disk values. The
/// ledger is replaced through a temporary file, so a crash cannot leave it truncated.
/// </summary>
public sealed partial class RecordStorageEvidenceFunction : IPipelineFunction
{
    internal const int RequiredSamples = 5;
    private const int LedgerRows = 50;
    private const long GiB = 1024L * 1024 * 1024;

    // Unit factors of the script's size_to_bytes, upper-cased as it upper-cases the value.
    private static readonly Dictionary<string, double> s_unitFactors = new(StringComparer.Ordinal)
    {
        [""] = 1,
        ["B"] = 1,
        ["BYTE"] = 1,
        ["BYTES"] = 1,
        ["K"] = 1e3,
        ["KB"] = 1e3,
        ["KIB"] = 1024,
        ["M"] = 1e6,
        ["MB"] = 1e6,
        ["MIB"] = 1048576,
        ["G"] = 1e9,
        ["GB"] = 1e9,
        ["GIB"] = 1073741824,
        ["T"] = 1e12,
        ["TB"] = 1e12,
        ["TIB"] = 1099511627776
    };

    [GeneratedRegex("^[a-zA-Z0-9_.-]+\\z")]
    private static partial Regex Identifier();

    [GeneratedRegex("^[0-9]+\\z")]
    private static partial Regex Digits();

    [GeneratedRegex("^[0-9]+([.][0-9]+)?")]
    private static partial Regex LeadingNumber();

    private readonly IPipelineFunctionTools _tools;
    private readonly TimeProvider _time;

    public RecordStorageEvidenceFunction()
        : this(PipelineFunctionTools.Default, TimeProvider.System)
    {
    }

    internal RecordStorageEvidenceFunction(IPipelineFunctionTools tools, TimeProvider time)
    {
        _tools = tools;
        _time = time;
    }

    public string Name => PipelineFunctionCatalog.RecordStorageEvidence.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = ReadInputs(context);
        if (input.Failure is not null)
            return PipelineFunctionResult.Failure(input.Failure);
        var (scope, runId, builder, workDirectory) = (input.Scope, input.RunId, input.Builder, input.WorkDirectory);

        var measure = await MeasureAsync(context, builder, cancellationToken).ConfigureAwait(false);
        if (measure.Failure is not null)
            return PipelineFunctionResult.Failure(measure.Failure);

        if (await ResidualFailureAsync(context, measure, input.Slug, runId).ConfigureAwait(false) is { } residual)
            return PipelineFunctionResult.Failure(residual);

        var collectedAt = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var row = string.Join('\t', collectedAt, runId, builder, measure.CacheTotal, measure.CacheReclaimable,
            measure.DiskUsedPercent, measure.DiskFree, "0", "0");
        var ledger = Path.Combine(workDirectory, "storage-evidence", $"{scope}-v2.tsv");
        var recent = await WriteLedgerAsync(ledger, row, runId, builder, cancellationToken).ConfigureAwait(false);

        await context.Log(
            $"Storage evidence recorded: scope={scope} run={runId} builder={builder} cache={measure.CacheTotal} "
            + $"reclaimable={measure.CacheReclaimable} diskUsed={measure.DiskUsedPercent}% free={measure.DiskFree} "
            + $"residualContainers=0 residualVolumes=0 samples={recent.Count}/{RequiredSamples}").ConfigureAwait(false);
        if (recent.Count < RequiredSamples)
        {
            await context.Log($"Storage plateau evidence is waiting for {RequiredSamples - recent.Count} additional successful QA run(s).").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }

        return await VerifyPlateauAsync(context, recent, input, ledger).ConfigureAwait(false);
    }

    private sealed record Inputs(
        string? Failure, string Scope = "", string RunId = "", string Builder = "", string WorkDirectory = "",
        string Slug = "", long MaxCacheGib = 0, long GrowthGib = 0);

    /// <summary>The script's input checks, in its order; the first refusal wins.</summary>
    private static Inputs ReadInputs(PipelineFunctionContext context)
    {
        var scope = context.Parameters.GetValueOrDefault("scope") is { Length: > 0 } given ? given : "qa";
        var runId = FunctionShell.Input(context, "run_id", "BUILD_BUILDID") ?? string.Empty;
        var builder = FunctionShell.Input(context, "builder", "AETHEUS_BUILDX_BUILDER") ?? FunctionShell.Variable(context, "BUILDX_BUILDER");
        var workDirectory = FunctionShell.Variable(context, "AETHEUS_AGENT_WORK_DIRECTORY");

        if (!Identifier().IsMatch(scope))
            return new Inputs("Storage evidence scope is invalid.");
        if (!Identifier().IsMatch(runId))
            return new Inputs("Storage evidence run id is missing or invalid.");
        if (!TryReadThresholds(context, out var maxCacheGib, out var growthGib))
            return new Inputs("Storage evidence thresholds must be non-negative integers.");
        if (builder is null)
            return new Inputs("AETHEUS_BUILDX_BUILDER is missing; refusing to attribute cache evidence to an unknown builder.");
        if (workDirectory is null || !Directory.Exists(workDirectory))
            return new Inputs("AETHEUS_AGENT_WORK_DIRECTORY is missing or unavailable; evidence would not survive workspace cleanup.");
        var slug = DeployIdentityFunction.Slug(FunctionShell.Input(context, "project_name", "BUILD_PROJECTNAME") ?? string.Empty);
        if (slug.Length == 0)
            return new Inputs("BUILD_PROJECTNAME is missing or has no usable character; the QA stacks cannot be named.");
        return new Inputs(null, scope, runId, builder, workDirectory, slug, maxCacheGib, growthGib);
    }

    private static bool TryReadThresholds(PipelineFunctionContext context, out long maxCacheGib, out long growthGib)
    {
        var maxCacheRaw = FunctionShell.Input(context, "max_cache_gib", "AETHEUS_STORAGE_MAX_CACHE_GIB") ?? "80";
        var growthRaw = FunctionShell.Input(context, "plateau_growth_gib", "AETHEUS_STORAGE_PLATEAU_GROWTH_GIB") ?? "5";
        maxCacheGib = 0;
        growthGib = 0;
        return Digits().IsMatch(maxCacheRaw) && Digits().IsMatch(growthRaw)
            && long.TryParse(maxCacheRaw, CultureInfo.InvariantCulture, out maxCacheGib)
            && long.TryParse(growthRaw, CultureInfo.InvariantCulture, out growthGib);
    }

    /// <summary>Logs the exact residual QA Compose resources and returns the failure, or null when none remain.</summary>
    private async Task<string?> ResidualFailureAsync(PipelineFunctionContext context, Measure measure, string slug, string runId)
    {
        var stack = new Regex($"^{slug}-qa-(rollback-)?[0-9]+$", RegexOptions.CultureInvariant);
        var containers = await ResidualsAsync(context, measure.Containers!, 3, stack, runId).ConfigureAwait(false);
        var volumes = await ResidualsAsync(context, measure.Volumes!, 2, stack, runId).ConfigureAwait(false);
        var residualContainers = containers.Count(line => line.Trim().Length > 0);
        var residualVolumes = volumes.Count(line => line.Trim().Length > 0);
        if (residualContainers == 0 && residualVolumes == 0)
            return null;
        await context.Log("Exact residual QA Compose resources detected:", TaskLogLevel.Error).ConfigureAwait(false);
        if (containers.Count > 0)
            await context.Log("container\t" + string.Join('\n', containers), TaskLogLevel.Error).ConfigureAwait(false);
        if (volumes.Count > 0)
            await context.Log("volume\t" + string.Join('\n', volumes), TaskLogLevel.Error).ConfigureAwait(false);
        return $"{residualContainers} residual QA container(s) and {residualVolumes} volume(s) of this run or an older one remain on the host.";
    }

    private static async Task<PipelineFunctionResult> VerifyPlateauAsync(
        PipelineFunctionContext context, List<string[]> recent, Inputs input, string ledger)
    {
        var (verified, evaluation) = Evaluate(recent, unchecked(input.MaxCacheGib * GiB), unchecked(input.GrowthGib * GiB));
        if (!verified)
        {
            await context.Log(evaluation, TaskLogLevel.Error).ConfigureAwait(false);
            return PipelineFunctionResult.Failure($"Storage plateau verification failed; inspect {ledger} before any production rollout.");
        }
        await context.Log(evaluation).ConfigureAwait(false);
        await context.Log("Storage plateau verified across five successful QA runs.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    private sealed record Measure(
        string? Failure, string CacheTotal = "", string CacheReclaimable = "", string DiskUsedPercent = "",
        string DiskFree = "", string? Containers = null, string? Volumes = null);

    /// <summary>The script's inventory, in its order: Buildx, the root filesystem, then the Compose
    /// containers and volumes.</summary>
    private async Task<Measure> MeasureAsync(PipelineFunctionContext context, string builder, CancellationToken cancellationToken)
    {
        var docker = FunctionShell.Tool(context, "docker");
        var du = await CaptureAsync(context, docker, cancellationToken, "buildx", "du", "--builder", builder).ConfigureAwait(false);
        if (du is null)
            return new Measure($"Could not inventory Buildx builder '{builder}'.");
        var totalRaw = BuildxField(du, "total");
        var reclaimableRaw = BuildxField(du, "reclaimable");
        if (SizeToBytes(totalRaw) is not { } total)
            return new Measure($"Could not parse Buildx total '{totalRaw}'.");
        if (SizeToBytes(reclaimableRaw) is not { } reclaimable)
            return new Measure($"Could not parse Buildx reclaimable '{reclaimableRaw}'.");

        var df = await CaptureAsync(context, FunctionShell.Tool(context, "df"), cancellationToken, "-Pk", "/").ConfigureAwait(false);
        var fields = df?.Split('\n').Skip(1).FirstOrDefault()?.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields is not { Length: >= 3 } || !double.TryParse(fields[^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var freeKiB))
            return new Measure("Could not measure the root filesystem with df -Pk /.");
        var usedPercent = fields[^2].Replace("%", string.Empty, StringComparison.Ordinal);
        var free = Format(freeKiB * 1024);

        var containers = await CaptureAsync(context, docker, cancellationToken, "ps", "-a", "--filter", "label=com.docker.compose.project",
            "--format", "{{.ID}}\\t{{.Names}}\\t{{.Label \"com.docker.compose.project\"}}").ConfigureAwait(false);
        if (containers is null)
            return new Measure("Could not inventory Compose containers.");
        var volumes = await CaptureAsync(context, docker, cancellationToken, "volume", "ls", "--filter", "label=com.docker.compose.project",
            "--format", "{{.Name}}\\t{{.Label \"com.docker.compose.project\"}}").ConfigureAwait(false);
        if (volumes is null)
            return new Measure("Could not inventory Compose volumes.");
        return new Measure(null, total, reclaimable, usedPercent, free, containers, volumes);
    }

    /// <summary><c>$(tool args)</c> with the tool's stderr kept in the step log, as the script's shell
    /// showed it; null when the tool could not run or failed.</summary>
    private async Task<string?> CaptureAsync(
        PipelineFunctionContext context, string tool, CancellationToken cancellationToken, params string[] arguments)
    {
        var output = new StringBuilder();
        var result = await _tools.RunAsync(
            FunctionShell.StartInfo(context, tool, context.Workspace ?? Directory.GetCurrentDirectory(), arguments),
            (line, level) =>
            {
                if (line.StartsWith("[stderr] ", StringComparison.Ordinal))
                    return context.Log(line["[stderr] ".Length..], TaskLogLevel.Warning);
                if (level != TaskLogLevel.Warning && !line.StartsWith("[process] exit-code=", StringComparison.Ordinal))
                    output.Append(line).Append('\n');
                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && !result.TimedOut ? output.ToString().TrimEnd('\n') : null;
    }

    /// <summary><c>awk -F: 'tolower($1) == "&lt;name&gt;" { gsub(/^[[:space:]]+/, "", $2); print $2; exit }'</c>:
    /// the text between the first and second colon of the first such line, leading blanks removed.</summary>
    private static string BuildxField(string output, string name) =>
        output.Split('\n')
            .Select(line => line.Split(':'))
            .FirstOrDefault(parts => parts.Length > 1 && string.Equals(parts[0].ToLowerInvariant(), name, StringComparison.Ordinal))
            ?[1].TrimStart(' ', '\t', '\r', '\v', '\f') ?? string.Empty;

    /// <summary>The script's <c>size_to_bytes</c>: the first word of the value upper-cased, <c>*</c>
    /// dropped and <c>,</c> read as the decimal point, a number then one of the known units; the bytes
    /// rounded as <c>printf "%.0f"</c> rounds them, or null when the value is not a size.</summary>
    internal static string? SizeToBytes(string raw)
    {
        var value = (raw.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty)
            .ToUpperInvariant().Replace("*", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        var number = LeadingNumber().Match(value);
        if (!number.Success || !s_unitFactors.TryGetValue(value[number.Length..], out var factor))
            return null;
        return Format(double.Parse(number.Value, CultureInfo.InvariantCulture) * factor);
    }

    /// <summary><c>printf "%.0f"</c>: rounded half to even, no decimal point.</summary>
    private static string Format(double value) =>
        Math.Round(value, MidpointRounding.ToEven).ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>The script's <c>qa_residuals</c> over one inventory: the lines whose Compose project (the
    /// tab-separated <paramref name="column"/>, 1-based) is a QA stack of this run or an older one; a
    /// newer run's stack is counted on its own and reported.</summary>
    private static async Task<List<string>> ResidualsAsync(
        PipelineFunctionContext context, string inventory, int column, Regex stack, string runId)
    {
        var residuals = new List<string>();
        var concurrent = 0;
        var numericRun = Digits().IsMatch(runId);
        foreach (var line in inventory.Split('\n'))
        {
            var fields = line.Split('\t');
            var project = fields.Length >= column ? fields[column - 1] : string.Empty;
            if (!stack.IsMatch(project))
                continue;
            var id = project[(project.LastIndexOf('-') + 1)..];
            if (numericRun && AwkNumber(id) > AwkNumber(runId))
            {
                concurrent++;
                continue;
            }
            residuals.Add(line);
        }
        if (concurrent > 0)
            await context.Log($"Not counted: {concurrent} resource(s) of a newer QA run still in progress.", TaskLogLevel.Warning).ConfigureAwait(false);
        return residuals;
    }

    /// <summary>Replaces this run's row for this builder, keeps the last 50 rows (mode 600), and returns
    /// the builder's last five rows, oldest first.</summary>
    private static async Task<List<string[]>> WriteLedgerAsync(
        string ledger, string row, string runId, string builder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
        // awk's records: one per line, a final newline closing the last one rather than opening another.
        var content = File.Exists(ledger) ? await File.ReadAllTextAsync(ledger, cancellationToken).ConfigureAwait(false) : string.Empty;
        var rows = content.Length == 0
            ? []
            : (content.EndsWith('\n') ? content[..^1] : content).Split('\n')
                .Where(line => line.Split('\t') is not [_, var run, var owner, ..] || run != runId || owner != builder)
                .ToList();
        rows.Add(row);
        var kept = rows.Skip(Math.Max(0, rows.Count - LedgerRows)).ToList();
        var temporary = ledger + ".tmp";
        await File.WriteAllTextAsync(temporary, string.Concat(kept.Select(line => line + "\n")), cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, ledger, overwrite: true);
        var mine = kept.Select(line => line.Split('\t')).Where(fields => fields.Length > 2 && fields[2] == builder).ToList();
        return mine.Skip(Math.Max(0, mine.Count - RequiredSamples)).ToList();
    }

    /// <summary>The script's plateau evaluation over the builder's last five rows.</summary>
    private static (bool Verified, string Message) Evaluate(List<string[]> recent, long maxCache, long maxGrowth)
    {
        var overBudget = false;
        var residual = 0;
        double? min = null, max = null;
        for (var index = 0; index < recent.Count; index++)
        {
            var cache = AwkNumber(Field(recent[index], 4));
            if (cache > maxCache)
                overBudget = true;
            if (AwkNumber(Field(recent[index], 8)) != 0 || AwkNumber(Field(recent[index], 9)) != 0)
                residual = 1;
            if (index >= 2)
            {
                min = min is null ? cache : Math.Min(min.Value, cache);
                max = max is null ? cache : Math.Max(max.Value, cache);
            }
        }
        var growth = (max ?? 0) - (min ?? 0);
        var message = $"Storage plateau evaluation: recentRangeBytes={Format(growth)} maxAllowedBytes={Format(maxGrowth)} "
            + $"cacheBudgetBytes={Format(maxCache)} residual={residual}";
        return (!overBudget && residual == 0 && growth <= maxGrowth, message);
    }

    private static string Field(string[] fields, int column) => fields.Length >= column ? fields[column - 1] : string.Empty;

    /// <summary>A field read as a number the way awk reads one: its leading number, else 0.</summary>
    private static double AwkNumber(string text)
    {
        var match = LeadingNumber().Match(text.Trim());
        return match.Success ? double.Parse(match.Value, CultureInfo.InvariantCulture) : 0;
    }
}
