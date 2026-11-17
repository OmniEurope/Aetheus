// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Components.Inventory;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests.Inventory;

/// <summary>The coverage decision, over an in-memory file tree (a double of the repository, the real Git
/// reading is exercised by <c>ProjectInventoryIntegrationTests</c>).</summary>
public sealed class RepositoryInventoryReaderTests
{
    private const string NugetLock = """{"version":2,"dependencies":{"net10.0":{"Serilog":{"type":"Direct","resolved":"4.0.0"}}}}""";
    private const string NpmLock = """{"lockfileVersion":3,"packages":{"node_modules/a":{"version":"1.0.0"}}}""";

    private readonly VulnerabilityWatchOptions _options = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FileTree(Dictionary<string, string> files) : IProjectSourceReader
    {
        public List<string> Opened { get; } = [];

        /// <summary>The files listed by the tree whose content cannot be opened.</summary>
        public HashSet<string> Unopenable { get; } = [];

        public Task<ProjectSourceTree?> ReadTreeAsync(int repositoryId, string revision, CancellationToken ct) =>
            Task.FromResult<ProjectSourceTree?>(new ProjectSourceTree(
                "commit", [.. files.Select(file => new ProjectSourceFile(file.Key, "blob-" + file.Value.GetHashCode(StringComparison.Ordinal)))]));

        public Task<Stream?> OpenFileAsync(int repositoryId, string commitSha, string path, CancellationToken ct)
        {
            Opened.Add(path);
            if (Unopenable.Contains(path)) return Task.FromResult<Stream?>(null);
            return Task.FromResult<Stream?>(new MemoryStream(Encoding.UTF8.GetBytes(files[path])));
        }
    }

    private Task<RepositoryInventory?> ReadAsync(FileTree tree, string? fingerprint = null) =>
        new RepositoryInventoryReader(tree, Options.Create(_options), NullLogger<RepositoryInventoryReader>.Instance)
            .ReadAsync(1, "refs/heads/main", fingerprint, Ct);

    private Task<InventoryBuildResult> ResultAsync(Dictionary<string, string> files) =>
        ReadAsync(new FileTree(files)).ContinueWith(task => task.Result!.Result!, Ct, TaskContinuationOptions.None, TaskScheduler.Default);

    [Fact]
    public async Task ADotnetRepositoryWithItsLockFiles_IsComplete_AndTheProjectFileIsNotOpened()
    {
        var tree = new FileTree(new Dictionary<string, string>
        {
            ["src/App/App.csproj"] = "<Project />",
            ["src/App/packages.lock.json"] = NugetLock,
            ["Directory.Packages.props"] = "<Project />"
        });

        var result = (await ReadAsync(tree))!.Result!;

        Assert.Equal(ProjectInventoryCoverage.Complete, result.Coverage);
        Assert.Equal("serilog", Assert.Single(result.Items).PackageName);
        Assert.Equal("src/App/packages.lock.json", result.Items[0].SourcePath);
        Assert.Equal(["src/App/packages.lock.json"], tree.Opened);
    }

    [Fact]
    public async Task AProjectFileWithoutALockFile_IsPartial_WithItsCentralVersions()
    {
        var result = await ResultAsync(new Dictionary<string, string>
        {
            ["Directory.Packages.props"] = """<Project><ItemGroup><PackageVersion Include="Polly" Version="8.0.0" /></ItemGroup></Project>""",
            ["src/App.csproj"] = """<Project><ItemGroup><PackageReference Include="Polly" /></ItemGroup></Project>"""
        });

        Assert.Equal(ProjectInventoryCoverage.Partial, result.Coverage);
        Assert.Equal("8.0.0", Assert.Single(result.Items).Version);
        Assert.Equal("no-lock-file:dotnet=1", result.Reason);
    }

    [Fact]
    public async Task AWorkspaceMember_SharesTheLockFileOfTheRoot_AndItsNamesAreDirect()
    {
        var result = await ResultAsync(new Dictionary<string, string>
        {
            ["package-lock.json"] = NpmLock,
            ["package.json"] = """{"workspaces":["web"]}""",
            ["web/package.json"] = """{"dependencies":{"a":"^1.0.0"},"engines":{"node":">=20"}}"""
        });

        Assert.Equal(ProjectInventoryCoverage.Complete, result.Coverage);
        Assert.True(result.Items.Single(item => item.PackageName == "a").IsDirect);
        Assert.Contains(result.Items, item => item is { Kind: ProjectInventoryItemKind.Runtime, PackageName: "nodejs" });
    }

    [Fact]
    public async Task APackageJsonWithoutALockFile_StatesNoVersion_AndIsNotCovered()
    {
        var result = await ResultAsync(new Dictionary<string, string> { ["package.json"] = """{"dependencies":{"a":"^1.0.0"}}""" });

        Assert.Equal(ProjectInventoryCoverage.None, result.Coverage);
        Assert.Empty(result.Items);
        Assert.Equal("no-lock-file:javascript=1", result.Reason);
    }

    [Fact]
    public async Task ComposerWithItsLock_IsComplete_AndKeepsTheRuntimeOfComposerJson()
    {
        var result = await ResultAsync(new Dictionary<string, string>
        {
            ["composer.json"] = """{"require":{"php":"^8.2","acme/lib":"^1.0"}}""",
            ["composer.lock"] = """{"packages":[{"name":"acme/lib","version":"1.4.0"}]}"""
        });

        Assert.Equal(ProjectInventoryCoverage.Complete, result.Coverage);
        Assert.True(result.Items.Single(item => item.PackageName == "acme/lib").IsDirect);
        Assert.Equal("^8.2", result.Items.Single(item => item.Kind == ProjectInventoryItemKind.Runtime).Version);
    }

    [Fact]
    public async Task ARealGemfileLock_IsRead_WithExactVersionsOfTheGemSection()
    {
        var gemfile = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "inventory", "Gemfile.lock.fixture"), Ct);

        var result = await ResultAsync(new Dictionary<string, string> { ["Gemfile.lock"] = gemfile });

        Assert.Equal(ProjectInventoryCoverage.Complete, result.Coverage);
        Assert.Null(result.Reason);
        var gems = result.Items.Where(item => item.Ecosystem == InventoryEcosystems.RubyGems)
            .Select(item => $"{item.PackageName}@{item.Version}").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            ["actionpack@5.2.1", "crass@1.0.4", "loofah@2.2.0", "mini_portile2@2.4.0", "nokogiri@1.10.0", "rack@2.0.5"], gems);
        // The DEPENDENCIES section names what the Gemfile asked for; the rest came with it.
        Assert.Equal(
            ["loofah", "nokogiri", "rack"],
            result.Items.Where(item => item.IsDirect).Select(item => item.PackageName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AMalformedGemfileLock_NeverThrows_AndGivesNoPackage()
    {
        var result = await ResultAsync(new Dictionary<string, string>
        {
            ["Gemfile.lock"] = "GEM\n  specs:\n    ((((\n    rack\n    nokogiri ()\n    \0\0\n\t\tbroken (\n"
        });

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ATechnologyRecognizedButNotRead_IsNamedInTheReason()
    {
        var result = await ResultAsync(new Dictionary<string, string>
        {
            ["src/packages.lock.json"] = NugetLock,
            ["tools/Gemfile.lock"] = "GEM",
            ["android/build.gradle"] = "plugins {}",
            ["scripts/pyproject.toml"] = "[project]"
        });

        Assert.Equal(ProjectInventoryCoverage.Partial, result.Coverage);
        Assert.Equal("technology-not-read:java=1, technology-not-read:python=1", result.Reason);
    }

    [Fact]
    public async Task ARepositoryOnlyWithAReadableTechnologyAbsent_IsNone_AndSaysWhy()
    {
        var result = await ResultAsync(new Dictionary<string, string> { ["README.md"] = "# Hello" });

        Assert.Equal(ProjectInventoryCoverage.None, result.Coverage);
        Assert.Equal("no-recognized-manifest=1", result.Reason);
    }

    [Fact]
    public async Task AManifestThatCannotBeParsed_MakesTheReadPartial_WithoutStoppingTheOthers()
    {
        var result = await ResultAsync(new Dictionary<string, string>
        {
            ["a/packages.lock.json"] = "{ not json",
            ["b/packages.lock.json"] = NugetLock
        });

        Assert.Equal(ProjectInventoryCoverage.Partial, result.Coverage);
        Assert.Single(result.Items);
        Assert.Equal("file-unreadable:dotnet=1", result.Reason);
    }

    [Fact]
    public async Task AManifestThatCannotBeOpened_NeverReadsAsAFileWithoutPackages()
    {
        var tree = new FileTree(new Dictionary<string, string>
        {
            ["Dockerfile"] = "FROM alpine:3.19",
            ["packages.lock.json"] = NugetLock
        });
        tree.Unopenable.Add("Dockerfile");

        var result = (await ReadAsync(tree))!.Result!;

        Assert.Equal(ProjectInventoryCoverage.Partial, result.Coverage);
        Assert.Single(result.Items);
        Assert.StartsWith("file-unreadable", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileOverItsLimit_IsNotParsed_AndSaysSo()
    {
        _options.MaxManifestBytes = 1024;
        var result = await ResultAsync(new Dictionary<string, string>
        {
            ["package-lock.json"] = new string(' ', 4096),
            ["b/packages.lock.json"] = NugetLock
        });

        Assert.Equal("file-too-large:javascript=1", result.Reason);
    }

    [Fact]
    public async Task MoreManifestsThanTheLimit_ReadsTheFirst_AndSaysSo()
    {
        _options.MaxManifestFiles = 2;
        var files = Enumerable.Range(0, 3).ToDictionary(index => $"p{index}/packages.lock.json", _ => NugetLock);

        var result = await ResultAsync(files);

        Assert.Equal("file-limit=1", result.Reason);
    }

    [Fact]
    public async Task AByteOrderMark_DoesNotBreakAJsonManifest()
    {
        var result = await ResultAsync(new Dictionary<string, string> { ["packages.lock.json"] = "﻿" + NugetLock });

        Assert.Equal(ProjectInventoryCoverage.Complete, result.Coverage);
    }

    [Fact]
    public async Task TheSameManifests_AreNotOpenedAgain_AndAnotherFileChangesNothing()
    {
        var files = new Dictionary<string, string> { ["packages.lock.json"] = NugetLock };
        var first = await ReadAsync(new FileTree(files));

        files["README.md"] = "another commit";
        var unchanged = new FileTree(files);
        var second = await ReadAsync(unchanged, first!.Fingerprint);
        files["packages.lock.json"] = NugetLock.Replace("4.0.0", "4.1.0", StringComparison.Ordinal);
        var third = await ReadAsync(new FileTree(files), first.Fingerprint);

        Assert.Null(second!.Result);
        Assert.Empty(unchanged.Opened);
        Assert.Equal("4.1.0", Assert.Single(third!.Result!.Items).Version);
    }

    [Fact]
    public async Task TheBranchHandler_AsksForARead_OnlyForTheDefaultBranch()
    {
        var trigger = Substitute.For<IProjectInventoryTrigger>();
        var handler = new GitBranchInventoryHandler(trigger);

        await handler.HandleAsync(Push("refs/heads/feature"), Ct);
        trigger.DidNotReceiveWithAnyArgs().Request(default);

        await handler.HandleAsync(Push("refs/heads/feature", "refs/heads/main"), Ct);
        await handler.HandleAsync(new GitBranchMovedEvent(7, 1, "s", "r", "main", "d", [new GitRefUpdate("refs/heads/main", "b", "a")]), Ct);
        trigger.Received(2).Request(7);
    }

    private static GitPushProcessedEvent Push(params string[] references) =>
        new(7, 1, "s", "r", "main", "d", [.. references.Select(reference => new GitRefUpdate(reference, "b", "a"))]);

    [Fact]
    public void TheTrigger_QueuesTheRead_OnlyWhenTheWatchIsOn()
    {
        var queue = Substitute.For<IBackgroundTaskQueue>();

        new ProjectInventoryTrigger(queue, Options.Create(new VulnerabilityWatchOptions { Enabled = false })).Request(7);
        queue.DidNotReceiveWithAnyArgs().Enqueue(default!);

        new ProjectInventoryTrigger(queue, Options.Create(new VulnerabilityWatchOptions { Enabled = true })).Request(7);
        queue.ReceivedWithAnyArgs(1).Enqueue(default!);
    }
}
