// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Recette R-18: the contractions still due live in <see cref="SchemaContractionRegistry"/> alone, and the screens read
/// them from there. These guards keep the registry true to the code: its migrations exist, and a column is mapped by the
/// entity exactly while its unmap step is still due.
/// </summary>
public sealed class SchemaContractionRegistryAuditTests
{
    private static IReadOnlySet<string> MigrationIds { get; } = typeof(AppDbContext).Assembly.GetTypes()
        .Select(type => type.GetCustomAttribute<MigrationAttribute>()?.Id)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Entries_AreWellFormed()
    {
        var pending = SchemaContractionRegistry.Pending;
        var problems = new List<string>();

        foreach (var duplicate in pending.GroupBy(step => step.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
            problems.Add($"{duplicate.Key} is listed twice.");

        foreach (var step in pending)
        {
            if (step.Columns.Count == 0) problems.Add($"{step.Id} names no column.");
            if (step.Step == SchemaContractionStep.Unmap && (step.DependsOnId is not null || step.RequiredAfterMigration is null))
                problems.Add($"{step.Id} is an unmap step: it waits for a migration's release, never for another step.");
            if (step.Step == SchemaContractionStep.Drop && step.RequiredAfterMigration is null && step.DependsOnId is null)
                problems.Add($"{step.Id} is a drop step that waits for nothing: name the unmap migration or the unmap step.");
            if (step.DependsOnId is { } dependsOn
                && !pending.Any(other => other.Id == dependsOn && other.Table == step.Table && other.Columns.SequenceEqual(step.Columns)))
                problems.Add($"{step.Id} depends on {dependsOn}, which is not a pending step of the same columns.");
        }

        Assert.True(problems.Count == 0, string.Join(" ", problems));
    }

    [Fact]
    public void EveryMigrationTheRegistryNames_ExistsInTheMigrationTree()
    {
        var named = SchemaContractionRegistry.Pending
            .SelectMany(step => new[] { step.ExpandMigration, step.RequiredAfterMigration })
            .Concat(SchemaContractionRegistry.ContractMigrations)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);

        var missing = named.Where(id => !MigrationIds.Contains(id)).ToList();

        Assert.True(missing.Count == 0, "The contraction registry names migrations that do not exist: " + string.Join(", ", missing));
    }

    [Fact]
    public void AColumnIsMapped_ExactlyWhileItsUnmapStepIsStillDue()
    {
        var model = BuildModel();
        var pending = SchemaContractionRegistry.Pending;
        var problems = new List<string>();

        foreach (var step in pending)
        {
            var unmapStillDue = step.Step == SchemaContractionStep.Unmap
                || pending.Any(other => other.Id == step.DependsOnId && other.Step == SchemaContractionStep.Unmap);
            foreach (var column in step.Columns)
            {
                var mapped = IsMapped(model, step.Table, column);
                if (unmapStillDue && !mapped)
                    problems.Add($"{step.Table}.{column} is not mapped any more: the unmap step {step.Id} is done, replace it by its drop step.");
                if (!unmapStillDue && mapped)
                    problems.Add($"{step.Table}.{column} is still mapped by the entity: the drop step {step.Id} cannot be due before its unmap step.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(" ", problems));
    }

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=_registry_check_;Username=_;Password=_")
            .Options;
        using var context = new AppDbContext(options);
        return context.GetService<IDesignTimeModel>().Model;
    }

    private static bool IsMapped(IModel model, string table, string column) =>
        model.GetEntityTypes().Any(entity =>
            entity.GetTableName() == table
            && entity.GetProperties().Any(property =>
                property.GetColumnName(StoreObjectIdentifier.Table(table, entity.GetSchema())) == column));
}
