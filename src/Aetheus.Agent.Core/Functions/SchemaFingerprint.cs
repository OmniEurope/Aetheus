// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Port of <c>deploy/scripts/schema-fingerprint.mjs</c>: the fingerprint of a database schema state,
/// the ordered list of applied EF migration ids and the SHA-256 of that list joined with <c>\n</c>.
/// The source tree and a live database (<c>__EFMigrationsHistory</c>) yield the same list, which is
/// what lets QA verify the state a delivery contract declares.
/// </summary>
internal static class SchemaFingerprint
{
    /// <summary><c>{ hash, migrations }</c> of a canonical migration list.</summary>
    public static JsonObject Of(IReadOnlyList<string> migrations) => new()
    {
        ["hash"] = Hash(migrations),
        ["migrations"] = new JsonArray([.. migrations.Select(migration => JsonValue.Create(migration))])
    };

    public static string Hash(IReadOnlyList<string> migrations) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', migrations))));

    /// <summary>The migration ids of a source directory: every <c>&lt;MigrationId&gt;.cs</c>, without the
    /// designer files and the model snapshot, in ordinal order (the script's default sort).</summary>
    public static List<string> FromSource(string directory) =>
        [.. Directory.EnumerateFileSystemEntries(directory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name.EndsWith(".cs", StringComparison.Ordinal)
                           && !name.EndsWith(".Designer.cs", StringComparison.Ordinal)
                           && !name.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Select(name => name[..^".cs".Length])
            .Order(StringComparer.Ordinal)];
}
