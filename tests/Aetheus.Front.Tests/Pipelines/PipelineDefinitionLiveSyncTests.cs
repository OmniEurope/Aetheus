// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pipelines;

/// <summary>
/// Recette R-181: a change made elsewhere reloads the edit page only when nothing is being edited;
/// with unsaved edits the page shows a banner instead of overwriting them.
/// </summary>
public sealed class PipelineDefinitionLiveSyncTests
{
    private static readonly PipelineDto Saved = new()
    {
        Id = 5,
        Name = "build",
        Description = "d",
        YamlDefinition = "stages: []",
        ProjectId = 2
    };

    private static PipelineModel Form() => new()
    {
        Name = "build",
        Description = "d",
        YamlDefinition = "stages: []",
        ProjectId = 2,
        SourceBranch = "main"
    };

    [Fact]
    public void FormEqualToTheSavedPipeline_HasNoUnsavedChanges() =>
        Assert.False(PipelineDefinitionLiveSync.HasUnsavedChanges(Form(), Saved, "main"));

    [Fact]
    public void EditedYaml_OrName_IsAnUnsavedChange()
    {
        var yaml = Form();
        yaml.YamlDefinition = "stages: [build]";
        var name = Form();
        name.Name = "deploy";

        Assert.True(PipelineDefinitionLiveSync.HasUnsavedChanges(yaml, Saved, "main"));
        Assert.True(PipelineDefinitionLiveSync.HasUnsavedChanges(name, Saved, "main"));
    }

    [Fact]
    public void Front009_AnEditedSourceBranch_IsAnUnsavedChange()
    {
        // Changing only the branch in Settings used to read as clean, so a push from elsewhere reloaded the
        // page and overwrote the draft branch without the banner.
        var branch = Form();
        branch.SourceBranch = "release/2026.10";

        Assert.True(PipelineDefinitionLiveSync.HasUnsavedChanges(branch, Saved, "main"));
    }

    [Fact]
    public void NothingLoadedYet_IsNotAnEdit() =>
        Assert.False(PipelineDefinitionLiveSync.HasUnsavedChanges(Form(), null, "main"));

    private static UpdatePipelineRequest Sent() => new()
    {
        Name = "build",
        Description = "d",
        YamlDefinition = "stages: []",
        ProjectId = 2,
        SourceBranch = " main "
    };

    [Fact]
    public void SelfSave001_StoredPipelineEqualToTheLastSubmittedUpdate_IsTheOwnSave() =>
        Assert.True(PipelineDefinitionLiveSync.IsOwnSave(Sent(), Saved with { SourceBranch = "main" }));

    [Fact]
    public void SelfSave001_AnyOtherStoredContent_IsARemoteChange()
    {
        Assert.False(PipelineDefinitionLiveSync.IsOwnSave(Sent(), Saved with { SourceBranch = "main", YamlDefinition = "stages: [x]" }));
        Assert.False(PipelineDefinitionLiveSync.IsOwnSave(Sent(), Saved with { SourceBranch = "develop" }));
        Assert.False(PipelineDefinitionLiveSync.IsOwnSave(Sent(), Saved with { SourceBranch = "main", Description = "other" }));
    }

    [Fact]
    public void SelfSave001_NothingSubmittedOrNothingStored_IsNotTheOwnSave()
    {
        Assert.False(PipelineDefinitionLiveSync.IsOwnSave(null, Saved));
        Assert.False(PipelineDefinitionLiveSync.IsOwnSave(Sent(), null));
    }
}
