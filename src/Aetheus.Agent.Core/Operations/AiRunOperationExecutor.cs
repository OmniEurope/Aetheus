// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Runs an AI task by calling the profile's provider HTTP API (ADR-023, amendment 2026-10-11): no
/// binary is installed nor started. The agent keeps the work it is placed for: it holds the workspace
/// (the run's checkout for a pipeline step, an authenticated ephemeral clone for a project task),
/// assembles the prompt from it (<see cref="AiPromptAssembler"/>), sends one completion through
/// <see cref="AiProviderClient"/>, then publishes the report, the gate verdict and, when asked, the
/// findings as SARIF. The API key arrives in the encrypted task environment and is masked out of every
/// line this executor writes.
/// </summary>
public sealed class AiRunOperationExecutor(
    IServerApiClient apiClient,
    IShellRunner shellRunner,
    IHttpClientFactory httpClientFactory,
    IOptions<AetheusAgentOptions> options,
    TimeProvider timeProvider,
    ILogger<AiRunOperationExecutor> logger) : IOperationExecutor
{
    /// <summary>Outbound client to the AI providers: no redirect (the request carries the key).</summary>
    public const string HttpClientName = "AetheusAiProvider";

    private readonly AetheusAgentOptions _options = options.Value;

    public bool CanHandle(OperationKind kind) => kind == OperationKind.AiRun;

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            kind, target, new Dictionary<string, string>(), timeoutSeconds, onOutput, cancellationToken);

    public async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (kind != OperationKind.AiRun || !OperationTargetValidator.IsValid(kind, target))
            return new ExecutorResult(-1, false);
        if (!AiRunConfiguration.TryRead(envVars, out var configuration, out var error))
        {
            await onOutput(error, TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var tempRoot = Path.Combine(
            _options.WorkDirectory, "ai-temp", Guid.NewGuid().ToString("N"));
        var ownsWorkspace = string.IsNullOrWhiteSpace(configuration.WorkingDirectory);
        var workingDirectory = ownsWorkspace
            ? Path.Combine(tempRoot, "workspace")
            : Path.GetFullPath(configuration.WorkingDirectory);
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(workingDirectory);
        // Every line this run writes goes through the key mask, whatever produced it.
        Task Masked(string line, TaskLogLevel level) => onOutput(AiProviderClient.Mask(line, configuration.ApiKey), level);
        var run = new RunContext(
            configuration, ownsWorkspace, workingDirectory, tempRoot,
            System.Diagnostics.Stopwatch.StartNew(), timeProvider.GetUtcNow().UtcDateTime, Masked);
        try
        {
            return await ExecuteConfiguredAsync(run, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CleanupTemporaryDirectory(tempRoot);
        }
    }

    private async Task<ExecutorResult> ExecuteConfiguredAsync(
        RunContext run, int timeoutSeconds, CancellationToken ct)
    {
        var configuration = run.Configuration;
        // First line of the step log: which profile this step runs with, and why (never silent).
        if (configuration.ProfileResolution is { } resolution)
            await run.Output(resolution, TaskLogLevel.Info).ConfigureAwait(false);
        string? baseCommitSha = null;
        if (configuration.SourceRepositoryPath is not null)
        {
            if (!run.OwnsWorkspace)
                throw new InvalidOperationException(
                    "AI source checkout cannot replace an existing pipeline workspace.");
            baseCommitSha = await CheckoutSourceAsync(configuration, run.WorkingDirectory, ct).ConfigureAwait(false);
        }

        string userPrompt;
        try
        {
            userPrompt = await AiPromptAssembler.BuildUserPromptAsync(
                configuration, run.WorkingDirectory, shellRunner, run.Output, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // Mandatory: an unreadable prompt_file is this step's failure, published like any other.
            return await FailAsync(run, ex.Message, false, baseCommitSha, ct).ConfigureAwait(false);
        }

        var request = new AiCompletionRequest(
            configuration.Model,
            AiPromptAssembler.SystemPrompt(configuration.Gate, configuration.PublishFindings),
            userPrompt,
            configuration.MaxOutputTokens,
            configuration.Effort);
        var remaining = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)) - run.Stopwatch.Elapsed;
        if (remaining < TimeSpan.FromSeconds(1)) remaining = TimeSpan.FromSeconds(1);
        await run.Output(
            $"AI request: {configuration.Provider} model {configuration.Model}, prompt {Encoding.UTF8.GetByteCount(userPrompt)} bytes, " +
            $"max {configuration.MaxOutputTokens} output tokens, effort {AiProviderCatalog.EffortToken(configuration.Effort) ?? "provider default"}.",
            TaskLogLevel.Info).ConfigureAwait(false);
        var client = new AiProviderClient(httpClientFactory.CreateClient(HttpClientName));
        var outcome = await client.CompleteAsync(
            new AiProviderSettings(configuration.Provider, configuration.BaseUrl, configuration.ApiKey),
            request, remaining, ct).ConfigureAwait(false);
        foreach (var warning in outcome.Warnings)
            await run.Output(warning, TaskLogLevel.Warning).ConfigureAwait(false);
        if (outcome.Result is not { } completion)
            return await FailAsync(
                run, outcome.Error ?? "The AI provider returned nothing.", outcome.TimedOut, baseCommitSha, ct)
                .ConfigureAwait(false);
        return await PublishCompletionAsync(run, completion, baseCommitSha, ct).ConfigureAwait(false);
    }

    private async Task<ExecutorResult> PublishCompletionAsync(
        RunContext run, AiCompletionResult completion, string? baseCommitSha, CancellationToken ct)
    {
        var configuration = run.Configuration;
        if (completion.InputTokens is not null || completion.OutputTokens is not null)
            await run.Output(
                $"AI usage: {completion.InputTokens?.ToString() ?? "?"} input tokens, {completion.OutputTokens?.ToString() ?? "?"} output tokens.",
                TaskLogLevel.Info).ConfigureAwait(false);
        var truncated = completion.HitTokenLimit;
        if (truncated)
            await run.Output(
                $"The model stopped at the profile's {configuration.MaxOutputTokens} output tokens: the answer is incomplete.",
                TaskLogLevel.Warning).ConfigureAwait(false);
        var answer = AiProviderClient.Mask(completion.Text, configuration.ApiKey);
        var report = TruncateUtf8(answer, configuration.MaxOutputBytes, ref truncated);
        var verdict = ParseVerdict(answer);
        var succeeded = !string.IsNullOrWhiteSpace(report)
                        && (!configuration.Gate || verdict == AiVerdict.Pass);
        if (string.IsNullOrWhiteSpace(report))
            await run.Output("The AI provider returned an empty answer.", TaskLogLevel.Error).ConfigureAwait(false);
        if (configuration.Gate && verdict != AiVerdict.Pass)
            await run.Output("AI gate failed: VERDICT: PASS is required.", TaskLogLevel.Error).ConfigureAwait(false);
        succeeded &= await AiFindingsPublisher.PublishAsync(
            apiClient, timeProvider, logger, configuration, answer, run.TemporaryDirectory, run.StartedAt,
            run.Output, ct).ConfigureAwait(false);
        run.Stopwatch.Stop();
        return await PublishAsync(
            configuration,
            string.IsNullOrWhiteSpace(report) ? "The AI provider returned an empty answer." : report,
            verdict, succeeded, truncated, baseCommitSha, run.Stopwatch.ElapsedMilliseconds,
            run.Output, ct).ConfigureAwait(false);
    }

    private async Task<ExecutorResult> FailAsync(
        RunContext run, string error, bool timedOut, string? baseCommitSha, CancellationToken ct)
    {
        run.Stopwatch.Stop();
        var masked = AiProviderClient.Mask(error, run.Configuration.ApiKey);
        await run.Output(masked, TaskLogLevel.Error).ConfigureAwait(false);
        return await PublishAsync(
            run.Configuration, masked, AiVerdict.None, false, timedOut, baseCommitSha,
            run.Stopwatch.ElapsedMilliseconds, run.Output, ct).ConfigureAwait(false);
    }

    private void CleanupTemporaryDirectory(string tempRoot)
    {
        try
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove AI run temporary directory {Directory}", tempRoot);
        }
    }

    private sealed record RunContext(
        AiRunConfiguration Configuration,
        bool OwnsWorkspace,
        string WorkingDirectory,
        string TemporaryDirectory,
        System.Diagnostics.Stopwatch Stopwatch,
        DateTime StartedAt,
        Func<string, TaskLogLevel, Task> Output);

    private async Task<ExecutorResult> PublishAsync(
        AiRunConfiguration configuration,
        string report,
        AiVerdict verdict,
        bool succeeded,
        bool truncated,
        string? baseCommitSha,
        long durationMs,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        var published = await apiClient.PublishAiRunResultAsync(new PublishAiRunResultRequest
        {
            ServerTaskId = configuration.ServerTaskId,
            ProfileName = configuration.ProfileName,
            SendsDataExternally = configuration.SendsDataExternally,
            ReportMarkdown = report,
            Verdict = verdict,
            DurationMs = durationMs,
            SourceRepositoryId = configuration.SourceRepositoryId,
            BaseCommitSha = baseCommitSha,
            Truncated = truncated,
            Succeeded = succeeded
        }, ct).ConfigureAwait(false);
        if (published is null)
        {
            await onOutput("AI result could not be persisted.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        return new ExecutorResult(succeeded ? 0 : -1, false);
    }

    private async Task<string> CheckoutSourceAsync(
        AiRunConfiguration configuration,
        string workingDirectory,
        CancellationToken ct)
    {
        var sourcePath = configuration.SourceRepositoryPath
            ?? throw new InvalidOperationException("AI source repository path is missing.");
        var sourceUrl = $"{_options.ServerUrl.TrimEnd('/')}/{sourcePath.TrimStart('/')}";
        var clone = await shellRunner.RunExecAsync(
            "git",
            [
                "clone",
                "--single-branch",
                "--branch",
                configuration.SourceRef ?? "main",
                "--",
                sourceUrl,
                workingDirectory
            ],
            BuildGitAuthenticationEnvironment(configuration),
            workingDirectory,
            inheritEnvironment: false,
            64 * 1024,
            ct,
            TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (clone.ExitCode != 0)
            throw new InvalidOperationException("AI source repository checkout failed.");
        var revision = await shellRunner.RunExecAsync(
            "git",
            ["-C", workingDirectory, "rev-parse", "HEAD"],
            ct,
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var commit = revision.StdOut.Trim();
        if (revision.ExitCode != 0
            || commit.Length is not (40 or 64)
            || commit.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("AI source checkout did not produce a valid commit.");
        return commit;
    }

    private static Dictionary<string, string> BuildGitAuthenticationEnvironment(
        AiRunConfiguration configuration)
    {
        if (configuration.GitUsername is null || configuration.GitPassword is null)
            throw new InvalidOperationException("AI source checkout credentials are missing.");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{configuration.GitUsername}:{configuration.GitPassword}"));
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "http.extraHeader",
            ["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {basic}",
            ["GIT_TERMINAL_PROMPT"] = "0"
        };
    }

    internal static AiVerdict ParseVerdict(string report)
    {
        var verdict = AiVerdict.None;
        foreach (var line in report.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(line.Trim(), "VERDICT: PASS", StringComparison.Ordinal))
                verdict = verdict == AiVerdict.Fail ? AiVerdict.Fail : AiVerdict.Pass;
            else if (string.Equals(line.Trim(), "VERDICT: FAIL", StringComparison.Ordinal))
                verdict = AiVerdict.Fail;
        }
        return verdict;
    }

    private static string TruncateUtf8(string value, int maxBytes, ref bool truncated)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes) return value;
        truncated = true;
        return Encoding.UTF8.GetString(bytes.AsSpan(0, maxBytes));
    }
}
