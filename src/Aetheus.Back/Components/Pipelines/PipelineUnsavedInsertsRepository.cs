// SPDX-License-Identifier: EUPL-1.2
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The inserts waiting to be saved on the unit of work at one moment, as taken by
/// <see cref="IPipelineRepository.MarkUnsavedInserts"/>. Opaque to its callers: it only travels back to
/// <see cref="IPipelineRepository.DiscardUnsavedInsertsSince"/>.
/// </summary>
public readonly record struct UnsavedInsertsMark
{
    internal HashSet<object>? Inserts { get; init; }
}

/// <summary>
/// Lets a scheduling pass that is refused half-way drop what it had already queued. A stage creates its
/// tasks one step after the other onto the shared <c>AppDbContext</c> and nothing is saved before the stage
/// is complete; if step three is refused, the tasks of steps one and two sit in the change tracker, and the
/// next <c>SaveChanges</c> of the same scope (the stop of this run, or the next run the recovery sweep
/// handles with the same scope) would write them for a run that is being failed. The mark taken before
/// the pass says which inserts were already there, so only the pass's own are dropped. Detaching an insert
/// also clears the links tracked entities hold to it (a step run's <c>Task</c>, an owner's task list), which
/// the tests of this class pin: left in place, they would queue it again at the next change detection.
/// </summary>
internal sealed class PipelineUnsavedInsertsRepository(AppDbContext db)
{
    public UnsavedInsertsMark Mark() => new()
    {
        Inserts = AddedEntries().Select(entry => entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance)
    };

    public void DiscardSince(UnsavedInsertsMark mark)
    {
        // A mark nobody took says nothing about what was there before: dropping everything would be a guess.
        if (mark.Inserts is not { } before) return;
        foreach (var entry in AddedEntries().Where(entry => !before.Contains(entry.Entity)))
            entry.State = EntityState.Detached;
    }

    private List<EntityEntry> AddedEntries() =>
        [.. db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added)];
}
