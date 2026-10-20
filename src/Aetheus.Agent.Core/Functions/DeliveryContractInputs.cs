// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The inputs of <see cref="GenerateDeliveryContractFunction"/>: the script's positional arguments as
/// parameters (paths resolved in the workspace), and its environment inputs as parameters or the same
/// variables, refused with the script's usage message when one is missing.
/// </summary>
internal sealed record DeliveryContractInputs
{
    private const string Usage =
        "Usage: generate-delivery-contract <manifest> <output> <adapter-name> <adapter-version> "
        + "<candidate-prefix> <source-sha> <migrations-directory> <artifact>...";

    public string? Error { get; private init; }
    public string Manifest { get; private init; } = string.Empty;
    public string Output { get; private init; } = string.Empty;
    public string AdapterName { get; private init; } = string.Empty;
    public string AdapterVersion { get; private init; } = string.Empty;
    public string CandidatePrefix { get; private init; } = string.Empty;
    public string SourceSha { get; private init; } = string.Empty;
    public string MigrationsDirectory { get; private init; } = string.Empty;
    public IReadOnlyList<string> Artifacts { get; private init; } = [];
    public bool Bootstrap { get; private init; }
    public string VersionMode { get; private init; } = string.Empty;
    public string BaselineSchemaHash { get; private init; } = string.Empty;
    public string BaselineSchemaMigrationsB64 { get; private init; } = string.Empty;

    public static DeliveryContractInputs Read(PipelineFunctionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var workspace = context.Workspace!;
        var sourceSha = FunctionShell.Input(context, "source_sha", "BUILD_SOURCEVERSION") ?? string.Empty;
        var artifacts = context.Parameters["artifacts"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var migrations = context.Parameters["migrations_dir"];
        if (!WriteIntegrationManifestFunction.FullRevision().IsMatch(sourceSha) || artifacts.Length == 0
            || HasEmptyParameter(context))
            return new DeliveryContractInputs { Error = Usage };

        return new DeliveryContractInputs
        {
            Manifest = Path.Combine(workspace, context.Parameters["manifest"]),
            Output = Path.Combine(workspace, context.Parameters["output"]),
            AdapterName = context.Parameters["adapter_name"],
            AdapterVersion = context.Parameters["adapter_version"],
            CandidatePrefix = context.Parameters["candidate_prefix"],
            SourceSha = sourceSha,
            MigrationsDirectory = migrations == "-" ? "-" : Path.Combine(workspace, migrations),
            Artifacts = [.. artifacts.Select(artifact => Path.Combine(workspace, artifact))],
            Bootstrap = (FunctionShell.Input(context, "bootstrap", "DELIVERY_BASELINE_BOOTSTRAP") ?? "true") == "true",
            VersionMode = FunctionShell.Input(context, "version_mode", "DELIVERY_CANDIDATE_VERSION_MODE") ?? string.Empty,
            BaselineSchemaHash = FunctionShell.Variable(context, "DELIVERY_BASELINE_SCHEMA_HASH") ?? string.Empty,
            BaselineSchemaMigrationsB64 = FunctionShell.Variable(context, "DELIVERY_BASELINE_SCHEMA_MIGRATIONS_B64") ?? string.Empty
        };
    }

    private static bool HasEmptyParameter(PipelineFunctionContext context) =>
        new[] { "manifest", "output", "adapter_name", "adapter_version", "candidate_prefix", "migrations_dir" }
            .Any(name => string.IsNullOrEmpty(context.Parameters.GetValueOrDefault(name)));

    /// <summary>The production release's migration list, carried in base64 because a setvariable line
    /// holds one line: decoded as Node.js' <c>Buffer.from(value, "base64")</c> does (characters outside
    /// the alphabet skipped, padding optional, URL-safe letters accepted), then one id per non-blank
    /// line.</summary>
    public IReadOnlyList<string> BaselineMigrations()
    {
        var alphabet = new StringBuilder();
        foreach (var character in BaselineSchemaMigrationsB64)
        {
            if (character == '=')
                break;
            if (char.IsAsciiLetterOrDigit(character) || character is '+' or '/')
                alphabet.Append(character);
            else if (character is '-' or '_')
                alphabet.Append(character == '-' ? '+' : '/');
        }
        // A dangling single character carries no whole byte; Node.js drops it.
        if (alphabet.Length % 4 == 1)
            alphabet.Length--;
        while (alphabet.Length % 4 != 0)
            alphabet.Append('=');
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(alphabet.ToString()));
        return [.. text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];
    }
}
