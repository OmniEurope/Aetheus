// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Tasks;

/// <summary>Recette R-12: the link between an AI run (a server task) and the AI task definition it runs. The run is
/// created with the definition's id in its protected environment (the agent hands it back with the result), and this
/// is the one reader of it, so the live task feed can tell the AI task list which row a run in progress belongs to
/// without the list matching on the definition's display name, which a rename changes.</summary>
internal static class AiRunDefinitionLink
{
    /// <summary>The environment variable that carries the definition id of an AI run.</summary>
    internal const string EnvironmentKey = "AETHEUS_AI_TASK_DEFINITION_ID";

    /// <summary>The definition id of <paramref name="task"/> when it is an AI run created from a definition; null for
    /// any other task, a run of a pipeline step (no definition) and an environment that cannot be read.</summary>
    internal static int? Read(IEncryptionService encryption, ServerTask task)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(task);
        if (task.Operation != OperationKind.AiRun) return null;
        try
        {
            var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(
                TaskEnvProtection.Unprotect(encryption, task.EnvironmentVariables));
            return environment is not null
                && environment.TryGetValue(EnvironmentKey, out var text)
                && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? id
                : null;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException)
        {
            // An environment that cannot be read leaves the run without a row to match: it is never guessed.
            return null;
        }
    }
}
