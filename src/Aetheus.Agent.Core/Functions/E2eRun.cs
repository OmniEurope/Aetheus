// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>The inputs of one <c>run-qa-e2e-suite</c> call and the names derived from them, with the
/// script's refusals in its order and words.</summary>
internal sealed record E2eRun(
    string Label,
    string Front,
    string Back,
    string PasswordFile,
    string Image,
    string ExpectedCommit,
    string TestProject,
    string PreserveDatabase,
    string PreviousPayloadOnly,
    string TestFilter,
    string Workers,
    string Results)
{
    /// <summary>The only filter the script accepted besides none.</summary>
    internal const string RollbackRelevantFilter = "Category=RollbackRelevant";

    internal const string ContainerResults = "/tmp/aetheus-e2e-results";

    public string ReadyContainer => $"aetheus-{Label}-ready";

    public string RunnerContainer => $"aetheus-{Label}-tests";

    public string Trx => Path.Combine(Results, "e2e.trx");

    public string RawExitFile => Path.Combine(Results, "raw-exit-code");

    public static E2eRun From(PipelineFunctionContext context)
    {
        var label = context.Parameters["label"];
        return new E2eRun(
            label,
            context.Parameters["front"],
            context.Parameters["back"],
            Path.GetFullPath(context.Parameters["admin_password_file"], context.Workspace!),
            context.Parameters["image"],
            context.Parameters["expected_commit"],
            context.Parameters["test_project"],
            Optional(context, "preserve_database") ?? "false",
            Optional(context, "previous_payload_only") ?? "false",
            Optional(context, "test_filter") ?? string.Empty,
            FunctionShell.Input(context, "workers", "AETHEUS_E2E_WORKERS") ?? "1",
            Path.Combine(context.Workspace!, ".qa-test-results", label, "e2e"));
    }

    public string? Refusal() => ShapeRefusal() ?? FlagRefusal() ?? CommitRefusal();

    /// <summary>The run label and the worker count, the script's first two checks.</summary>
    internal string? ShapeRefusal()
    {
        if (!RunQaDeployedProbesFunction.Label().IsMatch(Label))
            return "Invalid E2E run label.";
        return Workers is "1" or "2" or "3" or "4" ? null : "AETHEUS_E2E_WORKERS must be 1 to 4.";
    }

    /// <summary>The two boolean flags and the test filter, in the script's order.</summary>
    internal string? FlagRefusal()
    {
        if (PreserveDatabase is not ("true" or "false"))
            return "Preserve-database flag must be true or false.";
        if (PreviousPayloadOnly is not ("true" or "false"))
            return "E2E_PREVIOUS_PAYLOAD_ONLY must be true or false.";
        return TestFilter.Length > 0 && TestFilter != RollbackRelevantFilter
            ? "E2E_TEST_FILTER must be empty or Category=RollbackRelevant."
            : null;
    }

    /// <summary>The expected source commit: exactly 40 hexadecimal characters.</summary>
    internal string? CommitRefusal()
    {
        if (ExpectedCommit.Length != 40)
            return "Expected E2E source commit must contain 40 hexadecimal characters.";
        return ExpectedCommit.All(char.IsAsciiHexDigit) ? null : "Expected E2E source commit is not hexadecimal.";
    }

    /// <summary>The suite container's <c>docker run</c> arguments, the script's environment plus the
    /// project to test.</summary>
    public string[] RunnerArguments(string network, string adminPassword) =>
    [
        "run", "--name", RunnerContainer, "--network", network,
        "-e", $"E2E_FRONTEND_URL=http://{Front}:8080",
        "-e", $"E2E_BACKEND_URL=http://{Back}:8080",
        "-e", "E2E_ADMIN_USER=admin",
        "-e", $"E2E_ADMIN_PASSWORD={adminPassword}",
        "-e", $"E2E_PRESERVE_DATABASE={PreserveDatabase}",
        "-e", $"E2E_WORKERS={Workers}",
        "-e", $"E2E_PREVIOUS_PAYLOAD_ONLY={PreviousPayloadOnly}",
        "-e", $"E2E_TEST_FILTER={TestFilter}",
        "-e", $"E2E_TEST_PROJECT={TestProject}",
        "-e", $"AETHEUS_E2E_RESULTS_DIR={ContainerResults}",
        "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_NOLOGO=1",
        Image,
        "sh", "-c", RunQaE2eSuiteFunction.RunnerScript
    ];

    /// <summary>The readiness container's loop, with the front spliced in as the script spliced it.</summary>
    public string[] ReadyArguments(string network) =>
    [
        "run", "--rm", "--name", ReadyContainer, "--network", network, Image,
        "sh", "-c", $"for i in $(seq 1 40); do curl -sf \"http://{Front}:8080/\" -o /dev/null && exit 0; sleep 3; done; exit 1"
    ];

    private static string? Optional(PipelineFunctionContext context, string name) =>
        context.Parameters.GetValueOrDefault(name) is { Length: > 0 } value ? value : null;
}
