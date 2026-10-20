// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text.Json;
using Aetheus.Agent.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

public class PluginLoaderTests : IDisposable
{
    private readonly string _pluginDir;

    public PluginLoaderTests()
    {
        _pluginDir = Path.Combine(Path.GetTempPath(), $"plugins-{Guid.NewGuid():N}");
    }

    [Fact]
    public async Task LoadPluginsAsync_MissingDirectory_DoesNotThrow()
    {
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance);
        await loader.LoadPluginsAsync(
            Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}"),
            TestContext.Current.CancellationToken);
        Assert.Empty(loader.LoadedPlugins);
    }

    [Fact]
    public async Task LoadPluginsAsync_EmptyDirectory_LoadsNothing()
    {
        Directory.CreateDirectory(_pluginDir);
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance);
        await loader.LoadPluginsAsync(_pluginDir, TestContext.Current.CancellationToken);
        Assert.Empty(loader.LoadedPlugins);
    }

    [Fact]
    public async Task LoadPluginsAsync_NoManifest_SkipsAllPlugins()
    {
        Directory.CreateDirectory(_pluginDir);
        await File.WriteAllBytesAsync(Path.Combine(_pluginDir, "fake.dll"), [0, 1, 2, 3], TestContext.Current.CancellationToken);
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance);
        await loader.LoadPluginsAsync(_pluginDir, TestContext.Current.CancellationToken);
        Assert.Empty(loader.LoadedPlugins);
    }

    [Fact]
    public async Task LoadPluginsAsync_HashMismatch_RejectsPlugin()
    {
        Directory.CreateDirectory(_pluginDir);
        await File.WriteAllBytesAsync(Path.Combine(_pluginDir, "fake.dll"), [0, 1, 2, 3], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_pluginDir, "plugins.manifest.json"),
            "{\"fake.dll\":\"0000000000000000000000000000000000000000000000000000000000000000\"}",
            TestContext.Current.CancellationToken);

        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance);
        await loader.LoadPluginsAsync(_pluginDir, TestContext.Current.CancellationToken);
        Assert.Empty(loader.LoadedPlugins);
    }

    [Fact]
    public async Task LoadPluginsAsync_ValidManifestAndAssembly_LoadsAndInitializesPlugin()
    {
        Directory.CreateDirectory(_pluginDir);
        var sourceAssembly = typeof(PluginLoaderTests).Assembly.Location;
        var pluginFileName = Path.GetFileName(sourceAssembly);
        var pluginPath = Path.Combine(_pluginDir, pluginFileName);
        File.Copy(sourceAssembly, pluginPath);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
            pluginPath, TestContext.Current.CancellationToken)));
        await File.WriteAllTextAsync(
            Path.Combine(_pluginDir, "plugins.manifest.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { [pluginFileName] = hash }),
            TestContext.Current.CancellationToken);
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance)
        {
            IsPluginDirectoryWritable = _ => false
        };

        await loader.LoadPluginsAsync(_pluginDir, TestContext.Current.CancellationToken);

        var plugin = Assert.Single(loader.LoadedPlugins);
        Assert.Equal("Test agent plugin", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        await loader.UnloadAllAsync(TestContext.Current.CancellationToken);
        Assert.Empty(loader.LoadedPlugins);
    }

    [Fact]
    public async Task LoadPluginsAsync_ManifestEntriesOutsideContract_AreRejectedAndValidOneStillLoads()
    {
        Directory.CreateDirectory(_pluginDir);
        var sourceAssembly = typeof(PluginLoaderTests).Assembly.Location;
        var pluginFileName = Path.GetFileName(sourceAssembly);
        File.Copy(sourceAssembly, Path.Combine(_pluginDir, pluginFileName));
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
            Path.Combine(_pluginDir, pluginFileName), TestContext.Current.CancellationToken)));
        var manifest = new Dictionary<string, string?>
        {
            [pluginFileName] = hash.ToLowerInvariant(),
            ["../escape.dll"] = hash,
            ["sub/nested.dll"] = hash,
            [@"sub\nested.dll"] = hash,
            ["short.dll"] = "abc",
            ["nonhex.dll"] = new string('z', 64),
            ["empty.dll"] = string.Empty,
            ["null.dll"] = null,
        };
        await File.WriteAllTextAsync(
            Path.Combine(_pluginDir, "plugins.manifest.json"),
            JsonSerializer.Serialize(manifest),
            TestContext.Current.CancellationToken);
        var logger = new PluginLoaderRecordingLogger();
        var loader = new PluginLoader(logger) { IsPluginDirectoryWritable = _ => false };

        await loader.LoadPluginsAsync(_pluginDir, TestContext.Current.CancellationToken);

        Assert.Equal("Test agent plugin", Assert.Single(loader.LoadedPlugins).Name);
        var warnings = logger.Messages(LogLevel.Warning);
        Assert.Equal(3, warnings.Count(message => message.Contains("path traversal detected", StringComparison.Ordinal)));
        Assert.Equal(4, warnings.Count(message => message.Contains("invalid SHA256 hash format", StringComparison.Ordinal)));
        Assert.DoesNotContain(warnings, message => message.Contains(pluginFileName, StringComparison.Ordinal));
        await loader.UnloadAllAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LoadPluginsAsync_NullManifest_RejectsEveryPlugin()
    {
        Directory.CreateDirectory(_pluginDir);
        var sourceAssembly = typeof(PluginLoaderTests).Assembly.Location;
        File.Copy(sourceAssembly, Path.Combine(_pluginDir, Path.GetFileName(sourceAssembly)));
        await File.WriteAllTextAsync(
            Path.Combine(_pluginDir, "plugins.manifest.json"), "null", TestContext.Current.CancellationToken);
        var logger = new PluginLoaderRecordingLogger();
        var loader = new PluginLoader(logger) { IsPluginDirectoryWritable = _ => false };

        await loader.LoadPluginsAsync(_pluginDir, TestContext.Current.CancellationToken);

        Assert.Empty(loader.LoadedPlugins);
        Assert.Contains(logger.Messages(LogLevel.Warning),
            message => message.Contains("failed hash verification", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadPluginsAsync_WritableDirectory_RefusesLoading()
    {
        Directory.CreateDirectory(_pluginDir);
        var logger = new PluginLoaderRecordingLogger();
        var loader = new PluginLoader(logger) { IsPluginDirectoryWritable = _ => true };

        await loader.LoadPluginsAsync(_pluginDir, TestContext.Current.CancellationToken);

        Assert.Empty(loader.LoadedPlugins);
        Assert.Contains(logger.Messages(LogLevel.Error),
            message => message.Contains("writable by the agent user", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnloadAllAsync_EmptyLoader_Completes()
    {
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance);
        await loader.UnloadAllAsync(TestContext.Current.CancellationToken);
        Assert.Empty(loader.LoadedPlugins);
    }

    [Fact]
    public void AddPluginLoader_RegistersSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        PluginLoader.AddPluginLoader(services);

        Assert.Contains(services, s => s.ServiceType == typeof(PluginLoader));
        Assert.Contains(services, s => s.ServiceType == typeof(IPluginLoader));

        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<PluginLoader>());
        Assert.Same(provider.GetRequiredService<PluginLoader>(), provider.GetRequiredService<IPluginLoader>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_pluginDir))
        {
            try { Directory.Delete(_pluginDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}

public sealed class TestAgentPlugin : IAgentPlugin
{
    public string Name => "Test agent plugin";
    public string Version => "1.0.0";
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class PluginLoaderRecordingLogger : ILogger<PluginLoader>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<string> Messages(LogLevel level) =>
        _entries.Where(entry => entry.Level == level).Select(entry => entry.Message).ToList();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => _entries.Add((logLevel, formatter(state, exception)));
}
