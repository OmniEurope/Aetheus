// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// A database schema state as the delivery gate reads it, port of <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/schema-fingerprint.mjs</c>:
/// the ordered list of applied EF migration ids and the SHA-256 of that list joined by line feeds
/// (UTF-8, as <c>hash.update(string)</c> encodes it).
/// </summary>
internal sealed record DeliverySchemaState(string Hash, IReadOnlyList<string> Migrations)
{
    /// <summary><c>fingerprint(migrations)</c>.</summary>
    public static DeliverySchemaState Of(IReadOnlyList<string> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', migrations)));
        return new DeliverySchemaState(Convert.ToHexStringLower(hash), migrations);
    }

    /// <summary><c>migrationsFromHistory(text)</c>: the raw psql output split on line feeds, each line
    /// trimmed as <c>String.prototype.trim</c> does, empty lines dropped, sorted like <c>.sort()</c>
    /// without comparator (UTF-16 code units).</summary>
    public static List<string> MigrationsFromHistory(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Split('\n')
            .Select(JsValue.Trim)
            .Where(line => line.Length > 0)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The <c>describe(state)</c> of the delivery gate for a state it computed itself.</summary>
    public string Describe() => $"{Hash} ({Migrations.Count} migrations)";

    /// <summary>The <c>describe(state)</c> of the delivery gate for a state read from a contract, with
    /// JavaScript's reads and conversions: <c>`${state.hash} (${state.migrations.length} migrations)`</c>.</summary>
    public static string Describe(object? state)
    {
        var hash = JsValue.ToJsString(JsValue.Get(state, "hash"));
        var count = JsValue.ToJsString(JsValue.Get(JsValue.Get(state, "migrations"), "length"));
        return $"{hash} ({count} migrations)";
    }
}
