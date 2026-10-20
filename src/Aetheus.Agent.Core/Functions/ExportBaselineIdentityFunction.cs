// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>export-baseline-identity</c> function, port of
/// <c>deploy/scripts/candidate/export-baseline-identity.sh</c> and of the Node program it embedded:
/// publishes the source revision and the schema state (fingerprint and migration list) of the deployed
/// baseline read from its restored delivery contract, so qualification migrates from what production
/// actually stands on, or attests a bootstrap when no contract was restored. A legacy contract without
/// <c>baseline.schemaAfter</c> has its migration list rebuilt from the Git tree of the deployed
/// revision, with the same Git commands, the same fingerprint (SHA-256 of the sorted names joined by
/// newlines) and the same messages as the script.
///
/// Differences, deliberate: the contract path is the <c>contract</c> parameter (default the script's
/// <c>.delivery-baseline/.pipeline-artifacts/delivery-contract.json</c>) and the migrations directory
/// the script fixed to <c>src/Aetheus.Back/Data/Migrations/</c> is the <c>migrations_dir</c>
/// parameter, needed only to rebuild a legacy contract (refused with its cause when absent). Node is
/// no longer required. A contract that is not JSON is refused with its path, where Node stopped on a
/// stack trace; a contract with a schema state but no <c>sourceSha</c> publishes an empty
/// <c>DELIVERY_BASELINE_SOURCE_SHA</c>, where the script published the text <c>undefined</c>; a
/// bootstrap publishes the three identity outputs empty (every declared output is published), where the
/// script left them unset.
/// </summary>
public sealed partial class ExportBaselineIdentityFunction : IPipelineFunction
{
    private const string DefaultContract = ".delivery-baseline/.pipeline-artifacts/delivery-contract.json";

    [GeneratedRegex("^[0-9a-fA-F]{40}\\z")]
    private static partial Regex FullSha();

    private readonly IPipelineFunctionTools _tools;

    public ExportBaselineIdentityFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal ExportBaselineIdentityFunction(IPipelineFunctionTools tools) => _tools = tools;

    public string Name => PipelineFunctionCatalog.ExportBaselineIdentity.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");

        var relative = context.Parameters.GetValueOrDefault("contract") is { Length: > 0 } given ? given : DefaultContract;
        var path = Path.Combine(context.Workspace, relative);
        // test -s: present and not empty.
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            await context.Log($"No deployed delivery contract at {relative}: this candidate is a bootstrap.").ConfigureAwait(false);
            context.SetOutput("DELIVERY_BASELINE_BOOTSTRAP", "true");
            // Every declared output is published; a bootstrap has no baseline identity to give.
            context.SetOutput("DELIVERY_BASELINE_SOURCE_SHA", string.Empty);
            context.SetOutput("DELIVERY_BASELINE_SCHEMA_HASH", string.Empty);
            context.SetOutput("DELIVERY_BASELINE_SCHEMA_MIGRATIONS_B64", string.Empty);
            return PipelineFunctionResult.Success();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException ex)
        {
            return PipelineFunctionResult.Failure($"The deployed delivery contract {relative} is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var contract = document.RootElement;
            if (contract.ValueKind == JsonValueKind.Null)
                return PipelineFunctionResult.Failure($"The deployed delivery contract {relative} is null.");

            var sourceSha = Property(contract, "sourceSha");
            string hash;
            IReadOnlyList<string> migrations;
            var deployed = Property(Property(contract, "baseline"), "schemaAfter");
            if (Property(deployed, "hash") is { } deployedHash && Truthy(deployedHash))
            {
                // The schema state production stands on becomes the state this candidate migrates from.
                if (Property(deployed, "migrations") is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() == 0)
                    return PipelineFunctionResult.Failure("The production schema state could not be established.");
                hash = Text(deployedHash);
                migrations = list.EnumerateArray().Select(JoinedText).ToList();
            }
            else
            {
                var rebuilt = await ReconstructAsync(context, contract, sourceSha, cancellationToken).ConfigureAwait(false);
                if (rebuilt.Failure is { } failure)
                    return PipelineFunctionResult.Failure(failure);
                (hash, migrations) = (rebuilt.Hash!, rebuilt.Migrations!);
            }

            context.SetOutput("DELIVERY_BASELINE_BOOTSTRAP", "false");
            context.SetOutput("DELIVERY_BASELINE_SOURCE_SHA", sourceSha is { } sha ? Text(sha) : string.Empty);
            context.SetOutput("DELIVERY_BASELINE_SCHEMA_HASH", hash);
            // Base64: an output travels as one line, so the migration list has to be a single token.
            context.SetOutput(
                "DELIVERY_BASELINE_SCHEMA_MIGRATIONS_B64",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', migrations))));
            return PipelineFunctionResult.Success();
        }
    }

    /// <summary>The schema state of a legacy contract, read from the migrations of its deployed
    /// revision in the checkout's Git history.</summary>
    private async Task<(string? Hash, IReadOnlyList<string>? Migrations, string? Failure)> ReconstructAsync(
        PipelineFunctionContext context, JsonElement contract, JsonElement? sourceSha, CancellationToken cancellationToken)
    {
        var sha = sourceSha is { ValueKind: JsonValueKind.String } value ? value.GetString()! : string.Empty;
        if (!FullSha().IsMatch(sha))
            return (null, null, "The deployed contract names no source revision; nothing to reconstruct from.");
        if (context.Parameters.GetValueOrDefault("migrations_dir") is not { Length: > 0 } directory)
            return (null, null,
                "The deployed contract has no schema state (baseline.schemaAfter) and no migrations_dir is set to reconstruct it from.");

        var git = FunctionShell.Tool(context, FunctionShell.Variable(context, "GIT") ?? "git");
        var present = await _tools.RunAsync(
            StartInfo(context, git, "cat-file", "-e", $"{sha}^{{commit}}"), (_, _) => Task.CompletedTask, cancellationToken)
            .ConfigureAwait(false);
        if (present.ExitCode != 0 || present.TimedOut)
            return (null, null,
                $"The deployed revision {sha} is not in this checkout, so the schema state of the legacy contract cannot be "
                + "reconstructed. Raise AETHEUS_GIT_HISTORY_DEPTH until it reaches that revision.");

        // ls-tree lists the entries of a directory only when its path ends with a slash.
        var tree = directory.EndsWith('/') ? directory : directory + "/";
        var listing = await FunctionShell.CaptureAsync(
            _tools, StartInfo(context, git, "ls-tree", "--name-only", sha, tree), cancellationToken).ConfigureAwait(false);
        if (listing is null)
            return (null, null, $"git ls-tree of {tree} at deployed revision {sha} failed.");

        var listed = listing.Split('\n')
            .Select(line => line.Trim().Split('/')[^1])
            .Where(name => name.EndsWith(".cs", StringComparison.Ordinal))
            .Where(name => !name.EndsWith(".Designer.cs", StringComparison.Ordinal) && !name.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Select(name => name[..^3])
            .Order(StringComparer.Ordinal)
            .ToList();
        if (listed.Count == 0)
            return (null, null, $"No migrations found at deployed revision {sha}.");

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', listed))));
        var schema = Property(contract, "schema") is { } schemaValue ? Text(schemaValue) : "undefined";
        await context.Log(
            $"Reconstructed the schema state of legacy contract schema {schema} from deployed revision {sha}: {listed.Count} migrations.")
            .ConfigureAwait(false);
        return (hash, listed, null);
    }

    private static ProcessStartInfo StartInfo(PipelineFunctionContext context, string git, params string[] arguments) =>
        FunctionShell.StartInfo(context, git, context.Workspace!, arguments);

    /// <summary><c>value?.name</c>: the property of an object, null for anything else.</summary>
    private static JsonElement? Property(JsonElement? value, string name) =>
        value is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty(name, out var property) ? property : null;

    /// <summary>JavaScript truthiness of a JSON value.</summary>
    private static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Length > 0,
        JsonValueKind.Number => value.GetDouble() != 0,
        JsonValueKind.True or JsonValueKind.Object or JsonValueKind.Array => true,
        _ => false
    };

    /// <summary>The value as a template literal writes it: a string as is, anything else as JSON.</summary>
    private static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    /// <summary>An array element as <c>Array.prototype.join</c> writes it: null is empty.</summary>
    private static string JoinedText(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? string.Empty : Text(value);
}
