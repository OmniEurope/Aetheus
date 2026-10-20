// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>verify-nminus1-agent-contract</c> function, port of
/// <c>deploy/scripts/verify-nminus1-agent-contract.sh</c>: the retained N-1 software agent must still
/// work against backend N. Its Linux x64 archive is checked against the manifest (commit, protocol 2,
/// size and SHA-256) and unpacked in a temporary directory; an admin session issues a one-hour
/// registration token; the agent is started with it and must enroll (the token used by a server)
/// within 60 polls one second apart, then claim, run and report a task within 60 more.
/// <list type="bullet">
/// <item>A failed proof is a compatibility finding: the agent log is shown, the attestation records
/// which proofs held, and <c>AGENT_CONTRACT_STATUS</c> is 1 where the script exited 10.</item>
/// <item>Anything else (input, manifest, archive, a request the backend refuses) fails the step, as
/// the script's <c>set -e</c> did.</item>
/// </list>
/// The agent is stopped (its whole process tree) and the temporary directory removed on every path.
/// </summary>
public sealed class VerifyNMinusOneAgentContractFunction : IPipelineFunction
{
    internal const int Polls = 60;
    private const string AgentExecutable = "Aetheus.Agent.Linux";

    private readonly IPipelineFunctionTools _tools;
    private readonly HttpClient _http;
    private readonly BackgroundProcessLauncher _launch;

    public VerifyNMinusOneAgentContractFunction()
        : this(PipelineFunctionTools.Default, FunctionHttp.Shared, BackgroundProcess.Start)
    {
    }

    internal VerifyNMinusOneAgentContractFunction(IPipelineFunctionTools tools, HttpClient http, BackgroundProcessLauncher launch)
    {
        _tools = tools;
        _http = http;
        _launch = launch;
    }

    public string Name => PipelineFunctionCatalog.VerifyNMinusOneAgentContract.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var proof = AgentProof.From(context);
        if (proof.Refusal() is { } refusal)
            return PipelineFunctionResult.Failure(refusal);

        var temporary = Directory.CreateTempSubdirectory("aetheus-agent-nminus1.").FullName;
        IBackgroundProcess? agent = null;
        try
        {
            var (archive, archiveError) = VerifyArchive(proof);
            if (archive is null)
                return PipelineFunctionResult.Failure(archiveError!);
            var archivePath = Path.Combine(proof.AgentRelease, archive);
            if (await UnpackAsync(context, archivePath, temporary, cancellationToken).ConfigureAwait(false) is { } unpackError)
                return PipelineFunctionResult.Failure(unpackError);
            var evidence = new AgentEvidence(proof, archive, await Sha256Async(archivePath, cancellationToken).ConfigureAwait(false));

            var session = new AgentContractSession(new FunctionHttp(_http), proof.BackendUrl, _tools);
            await session.SignInAsync(proof.PasswordFile, temporary, cancellationToken).ConfigureAwait(false);
            agent = Start(context, proof, temporary, session);
            var outcome = await session.ProveAsync(cancellationToken).ConfigureAwait(false);
            return await PublishAsync(context, evidence, outcome, temporary, cancellationToken).ConfigureAwait(false);
        }
        catch (FunctionHttpException ex)
        {
            return PipelineFunctionResult.Failure(ex.Message);
        }
        finally
        {
            if (agent is not null)
                await agent.DisposeAsync().ConfigureAwait(false);
            Directory.Delete(temporary, recursive: true);
        }
    }

    /// <summary>The script's inline Node: the manifest's Linux x64 archive, held to the commit, the
    /// protocol and its size and digest.</summary>
    internal static (string? Name, string? Error) VerifyArchive(AgentProof proof)
    {
        using var manifest = AgentReleaseManifest.Read(proof.AgentRelease);
        var root = manifest.RootElement;
        if (!AgentReleaseManifest.IsString(root, "commit", proof.ExpectedCommit))
            return (null, "N-1 agent manifest commit mismatch.");
        if (!AgentReleaseManifest.IsNumber(root, "protocolVersion", 2))
            return (null, "N-1 software agent does not implement the current backend protocol.");
        var archive = AgentReleaseManifest.Items(root, "archives").FirstOrDefault(item =>
            AgentReleaseManifest.IsString(item, "platform", "linux") && AgentReleaseManifest.IsString(item, "architecture", "x64"));
        if (archive.ValueKind == JsonValueKind.Undefined)
            return (null, "N-1 Linux agent archive is missing.");
        return AgentReleaseManifest.ArchiveMatches(proof.AgentRelease, archive)
            ? (AgentReleaseManifest.Text(archive, "fileName"), null)
            : (null, "N-1 Linux agent archive integrity mismatch.");
    }

    private async Task<string?> UnpackAsync(PipelineFunctionContext context, string archive, string temporary, CancellationToken cancellationToken)
    {
        var tar = FunctionShell.StartInfo(context, FunctionShell.Tool(context, "tar"), temporary, "-xzf", archive, "-C", temporary);
        var result = await _tools.RunAsync(tar, (line, level) => context.Log(line, level), cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && !result.TimedOut
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"tar could not unpack {archive} (exit code {result.ExitCode}).");
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The agent with its <c>appsettings.json</c>: the native executable when the archive
    /// ships one, else <c>dotnet Aetheus.Agent.Linux.dll</c>, its output in <c>agent.log</c>.</summary>
    private IBackgroundProcess Start(PipelineFunctionContext context, AgentProof proof, string temporary, AgentContractSession session)
    {
        var settings = new
        {
            Aetheus = new
            {
                ServerUrl = proof.BackendUrl,
                Name = $"qa-agent-nminus1-{session.RegistrationTokenId}",
                RegistrationToken = session.RegistrationToken,
                WorkDirectory = Path.Combine(temporary, "work"),
                PollingIntervalSeconds = 1,
                HeartbeatIntervalSeconds = 1,
                HeartbeatCollectionTimeoutSeconds = 10,
                MaxConcurrentTasks = 1
            }
        };
        File.WriteAllText(Path.Combine(temporary, "appsettings.json"), JsonSerializer.Serialize(settings, AgentEvidence.Indented));
        var native = Path.Combine(temporary, AgentExecutable);
        var startInfo = FunctionShell.IsExecutable(native)
            ? FunctionShell.StartInfo(context, native, temporary)
            : FunctionShell.StartInfo(context, FunctionShell.Tool(context, "dotnet"), temporary, AgentExecutable + ".dll");
        return _launch(startInfo, Path.Combine(temporary, "agent.log"));
    }

    private static async Task<PipelineFunctionResult> PublishAsync(
        PipelineFunctionContext context, AgentEvidence evidence, AgentContractOutcome outcome, string temporary,
        CancellationToken cancellationToken)
    {
        if (outcome.Failure is null)
        {
            await evidence.WriteAsync("Passed", outcome, cancellationToken).ConfigureAwait(false);
            await context.Log("Retained N-1 agent contract passed: heartbeat, claim/start and result publication.").ConfigureAwait(false);
            context.SetOutput("AGENT_CONTRACT_STATUS", "0");
            return PipelineFunctionResult.Success();
        }

        var log = Path.Combine(temporary, "agent.log");
        if (File.Exists(log))
            foreach (var line in await File.ReadAllLinesAsync(log, cancellationToken).ConfigureAwait(false))
                await context.Log(line, TaskLogLevel.Error).ConfigureAwait(false);
        await evidence.WriteAsync("Failed", outcome, cancellationToken).ConfigureAwait(false);
        await context.Log(outcome.Failure, TaskLogLevel.Error).ConfigureAwait(false);
        context.SetOutput("AGENT_CONTRACT_STATUS", "1");
        return PipelineFunctionResult.Success();
    }
}
