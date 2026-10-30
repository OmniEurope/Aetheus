// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>run-qa-deployed-probes</c> function, port of
/// the former <c>run-qa-deployed-integration-suite.sh</c> script: proves that a deployed QA backend
/// authenticates and reads through its migrated database. The two health endpoints must answer (a
/// technical failure otherwise, as the script's <c>set -e</c>); the five application probes (login,
/// bearer token, permissions naming the admin user and its Admin role, a non-empty project page) are
/// graded: they write <c>deployed-proof.json</c> and, when one fails, the
/// <c>Deployed QA probes (&lt;label&gt;)</c> entry of <c>failed-tests.txt</c>, and publish
/// <c>DEPLOYED_PROBE_STATUS</c> 1 where the script exited 10. Responses are kept beside the proof, as
/// the script kept them.
///
/// Difference, deliberate: the login body is serialized as JSON (the script spliced the password into a
/// <c>printf</c> template; the QA password is hexadecimal, so the bytes are the same).
/// </summary>
public sealed partial class RunQaDeployedProbesFunction : IPipelineFunction
{
    private readonly HttpClient _http;

    public RunQaDeployedProbesFunction()
        : this(FunctionHttp.Shared)
    {
    }

    internal RunQaDeployedProbesFunction(HttpClient http) => _http = http;

    [GeneratedRegex("^[a-zA-Z0-9_-]+$")]
    internal static partial Regex Label();

    [GeneratedRegex("^.*\"token\":\"([^\"]*)\".*$")]
    private static partial Regex TokenLine();

    [GeneratedRegex("\"username\"[ \\t\\v\\f\\r]*:[ \\t\\v\\f\\r]*\"admin\"")]
    private static partial Regex AdminUser();

    [GeneratedRegex("\"roles\"[ \\t\\v\\f\\r]*:[ \\t\\v\\f\\r]*\\[[^\\]]*\"Admin\"")]
    private static partial Regex AdminRole();

    [GeneratedRegex("\"items\"[ \\t\\v\\f\\r]*:[ \\t\\v\\f\\r]*\\[[ \\t\\v\\f\\r]*\\{")]
    private static partial Regex NonEmptyItems();

    public string Name => PipelineFunctionCatalog.RunQaDeployedProbes.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var backend = context.Parameters["backend_url"];
        var label = context.Parameters["label"];
        var passwordFile = Path.GetFullPath(context.Parameters["admin_password_file"], context.Workspace);
        if (LoopbackRefusal(backend) is { } refusal)
            return PipelineFunctionResult.Failure(refusal);
        if (!Label().IsMatch(label))
            return PipelineFunctionResult.Failure("Invalid deployed integration result label.");
        if (!File.Exists(passwordFile) || new FileInfo(passwordFile).Length == 0)
            return PipelineFunctionResult.Failure($"The admin password file is missing or empty: {passwordFile}");

        var results = Path.Combine(context.Workspace, ".qa-test-results", label, "deployed");
        if (Directory.Exists(results))
            Directory.Delete(results, recursive: true);
        Directory.CreateDirectory(results);

        var http = new FunctionHttp(_http);
        try
        {
            await http.GetAsync(backend + "/health/live", null, cancellationToken).ConfigureAwait(false);
            await http.GetAsync(backend + "/health/ready", null, cancellationToken).ConfigureAwait(false);
        }
        catch (FunctionHttpException ex)
        {
            return PipelineFunctionResult.Failure(ex.Message);
        }

        var passed = await ProbeAsync(context, http, backend, passwordFile, results, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(results, "deployed-proof.json"),
            JsonSerializer.Serialize(new { schema = 1, suite = label, executedProbes = 5, status = passed ? "Passed" : "Failed" }) + "\n",
            cancellationToken).ConfigureAwait(false);
        return await PublishAsync(context, label, results, passed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The script's port check: loopback HTTP, then digits only after the colon.</summary>
    internal static string? LoopbackRefusal(string backend)
    {
        string? port = null;
        foreach (var prefix in (string[])["http://127.0.0.1:", "http://localhost:"])
            if (backend.StartsWith(prefix, StringComparison.Ordinal))
                port = backend[prefix.Length..];
        if (port is null)
            return "QA backend URL must be loopback HTTP.";
        return port.Length > 0 && port.All(char.IsAsciiDigit) ? null : "QA backend URL must contain only a numeric port.";
    }

    /// <summary>The script's probe subshell: true when all five probes passed. A failing request or
    /// check is a failed probe, said in the log, never a technical failure.</summary>
    private static async Task<bool> ProbeAsync(
        PipelineFunctionContext context, FunctionHttp http, string backend, string passwordFile, string results,
        CancellationToken cancellationToken)
    {
        try
        {
            var password = (await File.ReadAllTextAsync(passwordFile, cancellationToken).ConfigureAwait(false)).TrimEnd('\n');
            var login = await http.PostJsonAsync(
                backend + "/api/auth/login",
                JsonSerializer.Serialize(new { email = AgentContractSession.QaAdminEmail, password }),
                null,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(results, "login.json"), login, cancellationToken).ConfigureAwait(false);
            var token = Token(login);
            if (token.Length == 0)
            {
                await context.Log("QA login did not return a bearer token.", TaskLogLevel.Error).ConfigureAwait(false);
                return false;
            }

            var permissions = await http.GetAsync(backend + "/api/users/me/permissions", token, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(results, "permissions.json"), permissions, cancellationToken).ConfigureAwait(false);
            var projects = await http.GetAsync(backend + "/api/projects?page=1&pageSize=1", token, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(results, "projects.json"), projects, cancellationToken).ConfigureAwait(false);

            return AnyLine(permissions, AdminUser()) && AnyLine(permissions, AdminRole()) && AnyLine(projects, NonEmptyItems());
        }
        catch (FunctionHttpException ex)
        {
            await context.Log(ex.Message, TaskLogLevel.Error).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary><c>sed -n 's/.*"token":"\([^"]*\)".*/\1/p'</c>: the last token of every line that has
    /// one, the lines joined.</summary>
    internal static string Token(string login) =>
        string.Join('\n', login.Split('\n').Select(line => TokenLine().Match(line)).Where(match => match.Success)
            .Select(match => match.Groups[1].Value));

    /// <summary><c>grep -Eq</c>: the pattern on any single line.</summary>
    private static bool AnyLine(string text, Regex pattern) => text.Split('\n').Any(pattern.IsMatch);

    private static async Task<PipelineFunctionResult> PublishAsync(
        PipelineFunctionContext context, string label, string results, bool passed, CancellationToken cancellationToken)
    {
        if (passed)
        {
            await context.Log($"Deployed QA integration {label} passed 5/5 probes against the migrated stack.").ConfigureAwait(false);
            context.SetOutput("DEPLOYED_PROBE_STATUS", "0");
            return PipelineFunctionResult.Success();
        }
        await context.Log($"Deployed QA integration {label} recorded failed application probes.", TaskLogLevel.Error).ConfigureAwait(false);
        // No TRX here: the probes are named as one failed entry, for the gate step to publish.
        await File.WriteAllTextAsync(Path.Combine(results, "failed-tests.txt"), $"Deployed QA probes ({label})\n", cancellationToken)
            .ConfigureAwait(false);
        context.SetOutput("DEPLOYED_PROBE_STATUS", "1");
        return PipelineFunctionResult.Success();
    }
}
