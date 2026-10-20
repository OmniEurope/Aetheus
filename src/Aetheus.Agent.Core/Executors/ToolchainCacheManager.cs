// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Aetheus.Agent.Core.Toolchains;

namespace Aetheus.Agent.Core.Executors;

internal static class ToolchainCacheManager
{
    private const int MaxEntriesPerToolchain = 8;
    private const long MaxBytesPerToolchain = 10L * 1024 * 1024 * 1024;

    internal static IReadOnlyList<KeyValuePair<string, string>> CreateMounts(
        string workDirectory,
        ContainerSpec spec,
        ToolchainResolution resolution,
        string homePath,
        ILogger logger,
        TimeProvider timeProvider) =>
        CreateMountsCore(workDirectory, spec, resolution, homePath, logger, timeProvider, OperatingSystem.IsWindows());

    // The mount creation itself, with the platform decision injected so it is asserted on any platform.
    internal static IReadOnlyList<KeyValuePair<string, string>> CreateMountsCore(
        string workDirectory,
        ContainerSpec spec,
        ToolchainResolution resolution,
        string homePath,
        ILogger logger,
        TimeProvider timeProvider,
        bool isWindows)
    {
        if (isWindows)
            return [];

        var mounts = new List<KeyValuePair<string, string>>(resolution.Caches.Count);
        var cacheNamespace = BuildNamespace(
            spec.CacheTrustDomain ?? $"run-{spec.WorkspaceKey}",
            RuntimeInformation.OSArchitecture);
        foreach (var cache in resolution.Caches)
        {
            EnsureMountParents(homePath, cache.Target, logger);
            var cacheRoot = Path.Combine(
                workDirectory,
                "toolchain-cache",
                cacheNamespace,
                cache.Name);
            var hostPath = Path.Combine(cacheRoot, cache.Key);
            if (Directory.Exists(hostPath)
                && (File.GetAttributes(hostPath) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(hostPath);
            if (Directory.Exists(hostPath)
                && GetDirectorySize(hostPath, MaxBytesPerToolchain + 1) > MaxBytesPerToolchain)
                Directory.Delete(hostPath, recursive: true);
            Directory.CreateDirectory(hostPath);
            SetCurrentLastWriteTimeUtc(hostPath, timeProvider);
            TrySetOwnerOnly(hostPath, logger);
            PruneEntries(cacheRoot, hostPath, MaxEntriesPerToolchain, MaxBytesPerToolchain);
            mounts.Add(new KeyValuePair<string, string>(hostPath, cache.Target));
        }
        return mounts;
    }

    internal static string BuildNamespace(string trustDomain, Architecture architecture)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(trustDomain));
        return $"{architecture.ToString().ToLowerInvariant()}-{Convert.ToHexStringLower(digest)[..24]}";
    }

    internal static void SetCurrentLastWriteTimeUtc(string directory, TimeProvider timeProvider) =>
        Directory.SetLastWriteTimeUtc(directory, timeProvider.GetUtcNow().UtcDateTime);

    internal static void PruneEntries(
        string cacheRoot,
        string currentPath,
        int maxEntries,
        long maxBytes)
    {
        if (!Directory.Exists(cacheRoot))
            return;

        var entries = Directory.EnumerateDirectories(cacheRoot)
            .Where(path => !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => new
            {
                Path = path,
                LastWrite = Directory.GetLastWriteTimeUtc(path),
                IsLink = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0,
                Bytes = GetDirectorySize(path, maxBytes + 1)
            })
            .OrderByDescending(entry => entry.LastWrite)
            .ToList();
        var retainedBytes = GetDirectorySize(currentPath, maxBytes + 1)
            + entries.Sum(entry => entry.Bytes);
        var retainedCount = entries.Count + 1;
        foreach (var entry in entries.OrderBy(item => item.LastWrite))
        {
            if (retainedCount <= maxEntries && retainedBytes <= maxBytes)
                break;
            Directory.Delete(entry.Path, recursive: !entry.IsLink);
            retainedCount--;
            retainedBytes -= entry.Bytes;
        }
    }

    private static long GetDirectorySize(string root, long stopAfter)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            return 0;

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                    continue;
                }

                total = checked(total + new FileInfo(path).Length);
                if (total > stopAfter)
                    return total;
            }
        }
        return total;
    }

    private static void EnsureMountParents(string homePath, string target, ILogger logger)
    {
        const string containerHomePrefix = "/home/aetheus/";
        if (!target.StartsWith(containerHomePrefix, StringComparison.Ordinal))
            return;

        var segments = target[containerHomePrefix.Length..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = homePath;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);
            Directory.CreateDirectory(current);
            TrySetOwnerOnly(current, logger);
        }
    }

    private static void TrySetOwnerOnly(string directory, ILogger logger) =>
        OwnerOnlyDirectory.TrySet(directory, (exception, failedDirectory) =>
            logger.LogWarning(
                exception,
                "Could not restrict toolchain cache directory {Directory} to owner-only permissions",
                failedDirectory));
}
