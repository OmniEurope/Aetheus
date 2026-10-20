// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class AiRunnerProfileConfiguration : IEntityTypeConfiguration<AiRunnerProfile>
{
    public void Configure(EntityTypeBuilder<AiRunnerProfile> builder)
    {
        builder.Property(profile => profile.Name).HasMaxLength(120).IsRequired();
        builder.Property(profile => profile.Description).HasMaxLength(500);
        builder.Property(profile => profile.Binary).HasMaxLength(260).IsRequired();
        builder.Property(profile => profile.ArgsTemplateJson).HasMaxLength(32_000).IsRequired();
        builder.Property(profile => profile.EnvironmentJsonEncrypted).HasMaxLength(64_000).IsRequired();
        // API profile columns (ADR-023, amendment 2026-10-11). The expand migration gives them database
        // defaults, so the previous blue-green colour, which does not know them, can still insert.
        builder.Property(profile => profile.Provider).HasMaxLength(32).IsRequired();
        builder.Property(profile => profile.BaseUrl).HasMaxLength(500).IsRequired();
        builder.Property(profile => profile.Model).HasMaxLength(200).IsRequired();
        builder.Property(profile => profile.ApiKeyEncrypted).HasMaxLength(16_000).IsRequired();
        builder.Property(profile => profile.ReasoningEffort).HasMaxLength(16).IsRequired();
        // Recette R3-7, nightly model list: nullable (expand-only) for the previous blue-green colour.
        builder.Property(profile => profile.AvailableModelsJson).HasMaxLength(120_000);
        builder.Property(profile => profile.ModelListError).HasMaxLength(1000);
        builder.HasIndex(profile => new { profile.OrganizationId, profile.Name }).IsUnique();
        builder.HasOne(profile => profile.Organization)
            .WithMany()
            .HasForeignKey(profile => profile.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AiTaskDefinitionConfiguration : IEntityTypeConfiguration<AiTaskDefinition>
{
    public void Configure(EntityTypeBuilder<AiTaskDefinition> builder)
    {
        builder.Property(definition => definition.Name).HasMaxLength(120).IsRequired();
        builder.Property(definition => definition.PromptTemplate).HasMaxLength(20_000).IsRequired();
        builder.Property(definition => definition.Schedule).HasMaxLength(120);
        // Nullable, so the previous blue-green colour, which does not map them, still inserts (expand-only).
        builder.Property(definition => definition.ContextPathsJson).HasMaxLength(16_000);
        builder.Property(definition => definition.GitRange).HasMaxLength(200);
        builder.HasIndex(definition => definition.ProfileId);
        builder.HasIndex(definition => new { definition.Enabled, definition.Schedule });
        builder.HasOne(definition => definition.Profile)
            .WithMany(profile => profile.TaskDefinitions)
            .HasForeignKey(definition => definition.ProfileId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(definition => definition.Project)
            .WithMany()
            .HasForeignKey(definition => definition.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(definition => definition.Server)
            .WithMany()
            .HasForeignKey(definition => definition.ServerId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.ToTable(table => table.HasCheckConstraint(
            "CK_AiTaskDefinitions_ExactlyOneOwner",
            "(\"ProjectId\" IS NOT NULL AND \"ServerId\" IS NULL) OR (\"ProjectId\" IS NULL AND \"ServerId\" IS NOT NULL)"));
    }
}

internal sealed class AiTaskTriggerConfiguration : IEntityTypeConfiguration<AiTaskTrigger>
{
    public void Configure(EntityTypeBuilder<AiTaskTrigger> builder)
    {
        builder.Property(trigger => trigger.EventType).HasMaxLength(120).IsRequired();
        builder.HasIndex(trigger => new { trigger.AiTaskDefinitionId, trigger.EventType }).IsUnique();
        builder.HasIndex(trigger => trigger.EventType);
        builder.HasOne(trigger => trigger.AiTaskDefinition)
            .WithMany(definition => definition.Triggers)
            .HasForeignKey(trigger => trigger.AiTaskDefinitionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AiRunResultConfiguration : IEntityTypeConfiguration<AiRunResult>
{
    public void Configure(EntityTypeBuilder<AiRunResult> builder)
    {
        builder.Property(result => result.ProfileName).HasMaxLength(120).IsRequired();
        builder.Property(result => result.ReportMarkdown).HasMaxLength(1_000_000).IsRequired();
        builder.Property(result => result.DiffPatch).HasMaxLength(1_000_000);
        builder.Property(result => result.BaseCommitSha).HasMaxLength(64);
        builder.Property(result => result.ProposedBranchName).HasMaxLength(255);
        builder.Property(result => result.ProposedCommitSha).HasMaxLength(64);
        builder.HasIndex(result => result.ServerTaskId).IsUnique();
        builder.HasIndex(result => new { result.AiTaskDefinitionId, result.CreatedAt });
        builder.HasIndex(result => new { result.PipelineRunId, result.CreatedAt });
        builder.HasOne(result => result.ServerTask)
            .WithOne()
            .HasForeignKey<AiRunResult>(result => result.ServerTaskId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(result => result.PipelineRun)
            .WithMany()
            .HasForeignKey(result => result.PipelineRunId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(result => result.AiTaskDefinition)
            .WithMany(definition => definition.Results)
            .HasForeignKey(result => result.AiTaskDefinitionId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
