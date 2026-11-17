// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// R-13: finds the folder of EF migrations the expand/contract gate reads, so a project that turns its
/// migration gate on needs no path written in its pipeline.
///
/// The step's directory is used as it is when it already holds migrations (files named after an EF
/// migration id) or the EF model snapshot: that is the declared path of a pipeline that sets one. Otherwise
/// it is read as the root of the sealed source and the folder holding the single <c>*ModelSnapshot.cs</c>
/// below it is the migrations folder (EF writes the snapshot beside the migrations). None or several
/// snapshots fail the gate with the reason: a gate that cannot tell which migrations are about to run
/// never waves them through.
/// </summary>
internal static class BlueGreenMigrationSources
{
    private const string SnapshotPattern = "*ModelSnapshot.cs";

    /// <summary>Build outputs, dependencies and version-control folders, which never hold the sources.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea"
    };

    /// <summary>The located folder, or the reason the gate cannot run; <paramref name="directory"/> must exist.</summary>
    internal static (string? Directory, string? Error) Locate(string directory)
    {
        if (HoldsMigrations(directory)) return (directory, null);
        var snapshots = FindSnapshots(directory);
        return snapshots.Count switch
        {
            1 => (Path.GetDirectoryName(snapshots[0]), null),
            0 => (null,
                $"Aucune migration EF trouvée sous '{directory}' (ni fichier de migration, ni *ModelSnapshot.cs) : "
                + "la porte expand/contract ne peut pas voir ce qui va s'exécuter. "
                + "Indiquez le dossier des migrations dans la variable APPLICATION_MIGRATIONS_DIR du pipeline."),
            _ => (null,
                $"Plusieurs dossiers de migrations EF trouvés sous '{directory}' : "
                + string.Join(", ", snapshots.Select(path => Path.GetRelativePath(directory, Path.GetDirectoryName(path)!)))
                + ". Indiquez celui de l'application dans la variable APPLICATION_MIGRATIONS_DIR du pipeline.")
        };
    }

    private static bool HoldsMigrations(string directory) =>
        BlueGreenMigrationGate.DiscoverMigrationIds(directory).Count > 0
        || Directory.EnumerateFiles(directory, SnapshotPattern).Any();

    private static List<string> FindSnapshots(string root)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            found.AddRange(Directory.EnumerateFiles(current, SnapshotPattern, options));
            foreach (var child in Directory.EnumerateDirectories(current, "*", options))
            {
                if (!SkippedFolders.Contains(Path.GetFileName(child)))
                    pending.Push(child);
            }
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }
}
