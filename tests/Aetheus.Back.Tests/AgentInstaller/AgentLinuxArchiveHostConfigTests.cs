// SPDX-License-Identifier: EUPL-1.2
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Aetheus.Back.Extensions;
using Aetheus.Back.Tests.Architecture;

namespace Aetheus.Back.Tests.AgentInstaller;

/// <summary> Install-agent-linux.sh renders its host configuration files from agent-host-config/,
/// next to it in the agent archive, and refuses to run without that folder or with a carriage
/// return in a template. Both ways the backend produces the Linux archive (dev build from the
/// publish output, prebuilt archive re-packed with the server-URL marker) must therefore ship the
/// whole deploy/agent-host-config tree of the repository, byte for byte once normalised to LF.
/// </summary>
public sealed class AgentLinuxArchiveHostConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aetheus-r249-{Guid.NewGuid():N}");
    private readonly List<string> _archives = [];

    private static string RepositoryHostConfig => Path.Combine(RepositoryScan.Root, "deploy", "agent-host-config");

    [Fact]
    public async Task DevArchive_ShipsEveryTemplateNextToTheInstaller_WithLfEndings()
    {
        var backContentRoot = CreateSolution(crlfTemplates: true);

        var archive = await AgentDownloadsExtensions.TryBuildLinuxAgentArchiveAsync(backContentRoot, "https://api.example.test");

        Assert.NotNull(archive);
        _archives.Add(archive);
        var entries = await ReadArchiveAsync(archive);
        Assert.True(entries.ContainsKey("install-agent-linux.sh"), "install-agent-linux.sh missing from the archive");
        AssertShipsRepositoryTree(entries);
    }

    [Fact]
    public async Task DevArchive_IsNotBuilt_WhenTheTemplatesAreMissing()
    {
        var backContentRoot = CreateSolution(crlfTemplates: false);
        Directory.Delete(Path.Combine(_root, "deploy", "agent-host-config"), recursive: true);

        var archive = await AgentDownloadsExtensions.TryBuildLinuxAgentArchiveAsync(backContentRoot, "https://api.example.test");

        Assert.Null(archive);
    }

    [Fact]
    public async Task PrebuiltArchive_IsRepackedWithTheCurrentTemplates_ReplacingStaleOnes()
    {
        CreateSolution(crlfTemplates: true);
        var prebuiltSource = Path.Combine(_root, "prebuilt");
        Directory.CreateDirectory(Path.Combine(prebuiltSource, "agent-host-config", "apache"));
        await File.WriteAllTextAsync(Path.Combine(prebuiltSource, "Aetheus.Agent.Linux.dll"), "binary", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(prebuiltSource, "install-agent-linux.sh"), "#!/bin/sh\nexit 1\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(prebuiltSource, "agent-host-config", "apache", "stale-template"), "stale\n", TestContext.Current.CancellationToken);
        var prebuilt = Path.Combine(_root, "aetheus-agent-linux-x64-v1.0.0.tar.gz");
        await using (var output = File.Create(prebuilt))
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            await TarFile.CreateFromDirectoryAsync(prebuiltSource, gzip, includeBaseDirectory: false, TestContext.Current.CancellationToken);
        }

        var archive = await AgentDownloadsExtensions.BuildPrebuiltLinuxArchiveWithMarkerAsync(
            prebuilt,
            "https://api.example.test",
            Path.Combine(_root, "deploy", "agent-installer", "install-agent-linux.sh"),
            Path.Combine(_root, "deploy", "agent-host-config"));

        _archives.Add(archive);
        var entries = await ReadArchiveAsync(archive);
        Assert.False(entries.ContainsKey("agent-host-config/apache/stale-template"), "a stale template survived the re-pack");
        Assert.Equal("https://api.example.test\n", Encoding.UTF8.GetString(entries[".aetheus-server-url"]));
        AssertShipsRepositoryTree(entries);
    }

    [Fact]
    public async Task PreparedArchive_IsBuiltOnceForTheSameInputs_WithItsHash()
    {
        CreateSolution(crlfTemplates: false);
        var prebuilt = await CreatePrebuiltArchiveAsync();
        var script = Path.Combine(_root, "deploy", "agent-installer", "install-agent-linux.sh");
        var hostConfig = Path.Combine(_root, "deploy", "agent-host-config");

        var first = await AgentDownloadsExtensions.GetPreparedLinuxArchiveAsync(prebuilt, "https://api.example.test", script, hostConfig);
        var again = await AgentDownloadsExtensions.GetPreparedLinuxArchiveAsync(prebuilt, "https://api.example.test", script, hostConfig);
        _archives.Add(first.Path);

        Assert.Equal(first, again);
        await using (var stream = File.OpenRead(first.Path))
            Assert.Equal(Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken)), first.Sha256);
        var entries = await ReadArchiveAsync(first.Path);
        Assert.Equal("https://api.example.test\n", Encoding.UTF8.GetString(entries[".aetheus-server-url"]));
    }

    [Fact]
    public async Task PreparedArchive_AnotherServerUrl_IsPreparedApart()
    {
        CreateSolution(crlfTemplates: false);
        var prebuilt = await CreatePrebuiltArchiveAsync();
        var hostConfig = Path.Combine(_root, "deploy", "agent-host-config");

        var first = await AgentDownloadsExtensions.GetPreparedLinuxArchiveAsync(prebuilt, "https://one.example.test", null, hostConfig);
        var second = await AgentDownloadsExtensions.GetPreparedLinuxArchiveAsync(prebuilt, "https://two.example.test", null, hostConfig);
        _archives.Add(first.Path);
        _archives.Add(second.Path);

        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal("https://two.example.test\n", Encoding.UTF8.GetString((await ReadArchiveAsync(second.Path))[".aetheus-server-url"]));
    }

    [Fact]
    public async Task PreparedArchive_ThatDisappeared_IsRefusedOnceThenPreparedAgain()
    {
        CreateSolution(crlfTemplates: false);
        var prebuilt = await CreatePrebuiltArchiveAsync();
        var hostConfig = Path.Combine(_root, "deploy", "agent-host-config");
        var first = await AgentDownloadsExtensions.GetPreparedLinuxArchiveAsync(prebuilt, "https://gone.example.test", null, hostConfig);
        File.Delete(first.Path);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            AgentDownloadsExtensions.GetPreparedLinuxArchiveAsync(prebuilt, "https://gone.example.test", null, hostConfig));
        var rebuilt = await AgentDownloadsExtensions.GetPreparedLinuxArchiveAsync(prebuilt, "https://gone.example.test", null, hostConfig);
        _archives.Add(rebuilt.Path);

        Assert.Equal("Prepared Linux agent archive disappeared before it could be served.", error.Message);
        Assert.True(File.Exists(rebuilt.Path));
        Assert.NotEqual(first.Path, rebuilt.Path);
    }

    [Fact]
    public async Task PreparedArchive_OfAnUnreadableArchive_FailsAndIsNotCached()
    {
        Directory.CreateDirectory(_root);
        var broken = Path.Combine(_root, "aetheus-agent-linux-x64-v0.0.1.tar.gz");
        await File.WriteAllTextAsync(broken, "not a gzip archive", TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            AgentDownloadsExtensions.GetPreparedLinuxArchiveAsync(broken, "https://broken.example.test", null, null));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            AgentDownloadsExtensions.GetPreparedLinuxArchiveAsync(broken, "https://broken.example.test", null, null));
    }

    private async Task<string> CreatePrebuiltArchiveAsync()
    {
        var prebuiltSource = Path.Combine(_root, "prebuilt");
        Directory.CreateDirectory(prebuiltSource);
        await File.WriteAllTextAsync(Path.Combine(prebuiltSource, "Aetheus.Agent.Linux.dll"), "binary", TestContext.Current.CancellationToken);
        var prebuilt = Path.Combine(_root, "aetheus-agent-linux-x64-v1.0.0.tar.gz");
        await using (var output = File.Create(prebuilt))
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            await TarFile.CreateFromDirectoryAsync(prebuiltSource, gzip, includeBaseDirectory: false, TestContext.Current.CancellationToken);
        }
        return prebuilt;
    }

    /// <summary>Every file of the repository tree (manifest + templates) is in the archive under
    /// agent-host-config/, with the repository bytes once CRLF is folded to LF, and nothing else is.</summary>
    private static void AssertShipsRepositoryTree(IReadOnlyDictionary<string, byte[]> entries)
    {
        var expected = Directory.GetFiles(RepositoryHostConfig, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => "agent-host-config/" + Path.GetRelativePath(RepositoryHostConfig, file).Replace('\\', '/'),
                file => File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal),
                StringComparer.Ordinal);
        var shipped = entries.Keys
            .Where(name => name.StartsWith("agent-host-config/", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), shipped.Order(StringComparer.Ordinal));
        foreach (var (name, content) in expected)
        {
            var bytes = entries[name];
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.Equal(content, Encoding.UTF8.GetString(bytes));
        }
    }

    /// <summary>A disposable solution root: a Debug agent output, the real installer, and the real
    /// template tree, optionally rewritten with CRLF endings as a Windows checkout would have them.</summary>
    private string CreateSolution(bool crlfTemplates)
    {
        var agentOutput = Path.Combine(_root, "src", "Aetheus.Agent.Linux", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(agentOutput);
        File.WriteAllText(Path.Combine(agentOutput, "Aetheus.Agent.Linux.dll"), "binary");
        Directory.CreateDirectory(Path.Combine(_root, "deploy", "agent-installer"));
        File.Copy(
            Path.Combine(RepositoryScan.Root, "deploy", "agent-installer", "install-agent-linux.sh"),
            Path.Combine(_root, "deploy", "agent-installer", "install-agent-linux.sh"));
        foreach (var file in Directory.GetFiles(RepositoryHostConfig, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(_root, "deploy", "agent-host-config", Path.GetRelativePath(RepositoryHostConfig, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var text = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
            File.WriteAllText(destination, crlfTemplates ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text);
        }

        var backContentRoot = Path.Combine(_root, "src", "Aetheus.Back");
        Directory.CreateDirectory(backContentRoot);
        return backContentRoot;
    }

    private static async Task<Dictionary<string, byte[]>> ReadArchiveAsync(string archive)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var input = File.OpenRead(archive);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);
        while (await reader.GetNextEntryAsync(copyData: true) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                continue;
            using var buffer = new MemoryStream();
            await entry.DataStream.CopyToAsync(buffer);
            var name = entry.Name.Replace('\\', '/');
            entries[name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name] = buffer.ToArray();
        }

        return entries;
    }

    public void Dispose()
    {
        foreach (var archive in _archives.Where(File.Exists))
            File.Delete(archive);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
