// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;

namespace Aetheus.Agent.Core.Tests;

public sealed class ContainerWorkspaceMaterializerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("aetheus-workspace-mirror-").FullName;

    [Fact]
    public void RemoveStaleEntries_DeletesOutputsRemovedFromSource()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        File.WriteAllText(Path.Combine(source, "kept.txt"), "source");
        File.WriteAllText(Path.Combine(destination, "kept.txt"), "old");
        File.WriteAllText(Path.Combine(destination, "deleted.txt"), "stale");
        Directory.CreateDirectory(Path.Combine(destination, "deleted-dir"));

        ContainerWorkspaceMaterializer.RemoveStaleEntries(source, destination, excludeProtectedMetadata: false);

        Assert.True(File.Exists(Path.Combine(destination, "kept.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "deleted.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, "deleted-dir")));
    }

    [Fact]
    public void RemoveStaleEntries_PreservesGitAndReadyMarkerDuringBidirectionalPublication()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "container")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(_root, "prepared")).FullName;
        Directory.CreateDirectory(Path.Combine(destination, ".git"));
        File.WriteAllText(Path.Combine(destination, ".git", "HEAD"), "immutable");
        File.WriteAllText(Path.Combine(destination, ".aetheus-container-workspace-ready"), "42");
        File.WriteAllText(Path.Combine(destination, "removed-by-container.txt"), "stale");

        ContainerWorkspaceMaterializer.RemoveStaleEntries(source, destination, excludeProtectedMetadata: true);

        Assert.True(File.Exists(Path.Combine(destination, ".git", "HEAD")));
        Assert.True(File.Exists(Path.Combine(destination, ".aetheus-container-workspace-ready")));
        Assert.False(File.Exists(Path.Combine(destination, "removed-by-container.txt")));
    }

    [Fact]
    public void RemoveStaleEntries_DeletesNestedOutputsRemovedFromSource_AndKeepsNestedGit()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "nested-source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(_root, "nested-destination")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "out", "deep"));
        File.WriteAllText(Path.Combine(source, "out", "kept.dll"), "new");
        File.WriteAllText(Path.Combine(source, "out", "type-changed"), "file now");
        Directory.CreateDirectory(Path.Combine(destination, "out", "deep"));
        File.WriteAllText(Path.Combine(destination, "out", "kept.dll"), "old");
        File.WriteAllText(Path.Combine(destination, "out", "obsolete.dll"), "stale");
        File.WriteAllText(Path.Combine(destination, "out", "deep", "obsolete.pdb"), "stale");
        Directory.CreateDirectory(Path.Combine(destination, "out", "type-changed"));
        Directory.CreateDirectory(Path.Combine(destination, "out", ".git"));
        File.WriteAllText(Path.Combine(destination, "out", ".git", "HEAD"), "submodule");

        ContainerWorkspaceMaterializer.RemoveStaleEntries(source, destination, excludeProtectedMetadata: true);

        Assert.True(File.Exists(Path.Combine(destination, "out", "kept.dll")));
        Assert.False(File.Exists(Path.Combine(destination, "out", "obsolete.dll")));
        Assert.False(File.Exists(Path.Combine(destination, "out", "deep", "obsolete.pdb")));
        Assert.False(Directory.Exists(Path.Combine(destination, "out", "type-changed")));
        Assert.True(File.Exists(Path.Combine(destination, "out", ".git", "HEAD")));
    }

    [Fact]
    public void RemoveStaleEntries_RemovesEntriesWhoseFileTypeChanged()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "type-source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(_root, "type-destination")).FullName;
        File.WriteAllText(Path.Combine(source, "file-now"), "file");
        Directory.CreateDirectory(Path.Combine(destination, "file-now"));
        Directory.CreateDirectory(Path.Combine(source, "directory-now"));
        File.WriteAllText(Path.Combine(destination, "directory-now"), "file");

        ContainerWorkspaceMaterializer.RemoveStaleEntries(source, destination, excludeProtectedMetadata: false);

        Assert.False(Directory.Exists(Path.Combine(destination, "file-now")));
        Assert.False(File.Exists(Path.Combine(destination, "directory-now")));
    }

    [Fact]
    public async Task PreparationLock_DoesNotSplitWhenForgetRacesWithWaiters_AndSelfEvicts()
    {
        var keyedLock = new WorkspacePreparationLock();
        var first = await keyedLock.AcquireAsync(42, TestContext.Current.CancellationToken);
        var secondTask = keyedLock.AcquireAsync(42, TestContext.Current.CancellationToken);
        keyedLock.ForgetIdle(42);
        var thirdTask = keyedLock.AcquireAsync(42, TestContext.Current.CancellationToken);

        Assert.False(secondTask.IsCompleted);
        Assert.False(thirdTask.IsCompleted);
        first.Dispose();
        var second = await secondTask;
        Assert.False(thirdTask.IsCompleted);
        second.Dispose();
        var third = await thirdTask;
        third.Dispose();

        Assert.Equal(0, keyedLock.EntryCount);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
