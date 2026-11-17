// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// A migration that reached production is never edited: EF Core records only its id in
/// <c>__EFMigrationsHistory</c>, so an edit changes what a fresh database gets without changing what an
/// existing one already ran, and the two silently drift apart. Corrections go in a new migration.
/// <para>
/// <c>shipped-migrations.sha256</c> (next to this file) lists every shipped migration file (<c>&lt;id&gt;.cs</c>
/// and <c>&lt;id&gt;.Designer.cs</c>) with the SHA-256 of its content, line endings normalized to LF so a CRLF
/// checkout hashes like the repository. This test recomputes each hash from <c>src/Aetheus.Back/Data/Migrations</c>
/// and fails when a listed file was changed, renamed or deleted. A migration that is not listed yet is allowed:
/// it has not shipped, so it may still be amended. The model snapshot is not listed (every migration changes it).
/// </para>
/// <para>
/// <b>Adding a migration to the manifest</b>: only when a release carrying it has reached <c>main</c>. Append
/// one line per file, <c>&lt;sha256 hex&gt; &lt;file name&gt;</c>, keeping the file sorted by name (ordinal). The hash
/// is that of the file as it is on <c>main</c>, line endings turned to LF: with Git Bash,
/// <c>tr -d '\r' &lt; src/Aetheus.Back/Data/Migrations/&lt;file&gt; | sha256sum</c>. Never rewrite an existing line to make
/// this test pass: a changed hash means a shipped migration was edited, which is the defect.
/// </para>
/// </summary>
public sealed class ShippedMigrationsImmutableAuditTests
{
    private static string MigrationDirectory => Path.Combine(RepositoryScan.Root, "src", "Aetheus.Back", "Data", "Migrations");

    private static string ManifestPath => Path.Combine(
        RepositoryScan.Root, "tests", "Aetheus.Back.Tests", "Architecture", "shipped-migrations.sha256");

    [Fact]
    public void EveryShippedMigrationFile_IsUnchanged()
    {
        var manifest = ReadManifest();
        Assert.True(manifest.Count >= 300, $"The shipped-migrations manifest is unexpectedly small ({manifest.Count} entries).");

        var violations = new List<string>();
        foreach (var (fileName, expected) in manifest)
        {
            var path = Path.Combine(MigrationDirectory, fileName);
            if (!File.Exists(path))
            {
                violations.Add($"{fileName}: missing (a shipped migration was renamed or deleted).");
                continue;
            }

            var actual = HashWithLfLineEndings(File.ReadAllBytes(path));
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                violations.Add($"{fileName}: content changed after it shipped (expected {expected[..12]}, found {actual[..12]}).");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Shipped migrations are immutable; put the correction in a new migration. " + string.Join(" ", violations));
    }

    [Fact]
    public void TheManifest_IsWellFormed_AndNamesBothFilesOfEachMigration()
    {
        var manifest = ReadManifest();
        var names = manifest.Select(entry => entry.FileName).ToHashSet(StringComparer.Ordinal);

        foreach (var name in names.Where(name => !name.EndsWith(".Designer.cs", StringComparison.Ordinal)))
        {
            var designer = name[..^".cs".Length] + ".Designer.cs";
            Assert.True(names.Contains(designer), $"The manifest lists {name} without {designer}.");
        }

        Assert.Equal(
            manifest.Select(entry => entry.FileName).Order(StringComparer.Ordinal),
            manifest.Select(entry => entry.FileName));
    }

    [Fact]
    public void TheHash_IgnoresLineEndings_ButNotContent()
    {
        var lf = "one\ntwo\n"u8.ToArray();
        var crlf = "one\r\ntwo\r\n"u8.ToArray();
        var edited = "one\r\ntwp\r\n"u8.ToArray();

        Assert.Equal(HashWithLfLineEndings(lf), HashWithLfLineEndings(crlf));
        Assert.NotEqual(HashWithLfLineEndings(lf), HashWithLfLineEndings(edited));
    }

    private static List<(string FileName, string Sha256)> ReadManifest()
    {
        Assert.True(File.Exists(ManifestPath), $"Missing manifest {ManifestPath}.");
        var entries = new List<(string FileName, string Sha256)>();
        foreach (var raw in File.ReadAllLines(ManifestPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(parts.Length == 2 && parts[0].Length == 64, $"Malformed manifest line: '{line}'.");
            Assert.Matches(@"^[0-9a-f]{64}$", parts[0]);
            Assert.Matches(@"^\d{14}_[A-Za-z0-9_]+(\.Designer)?\.cs$", parts[1]);
            entries.Add((parts[1], parts[0]));
        }

        Assert.Equal(entries.Count, entries.Select(entry => entry.FileName).Distinct(StringComparer.Ordinal).Count());
        return entries;
    }

    private static string HashWithLfLineEndings(byte[] content)
    {
        var normalized = new List<byte>(content.Length);
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == (byte)'\r' && i + 1 < content.Length && content[i + 1] == (byte)'\n')
            {
                continue;
            }

            normalized.Add(content[i]);
        }

        return Convert.ToHexString(SHA256.HashData(normalized.ToArray())).ToLower(CultureInfo.InvariantCulture);
    }
}
