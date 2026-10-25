// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;

namespace Aetheus.Agent.Core.Collectors;

/// <param name="storage">Names the agent's own Buildx builder so its BuildKit container is shown as
/// Aetheus's; null where the builder is not needed.</param>
/// <param name="runtimeHealth">The agent's record of its pipeline tasks, the only evidence that attributes a
/// Testcontainers reaper to a run (R-33); null recognises none.</param>
public sealed class DockerCollector(
    ILogger<DockerCollector> logger, IShellRunner shell, IDockerStorageMaintenance? storage = null,
    AgentRuntimeHealth? runtimeHealth = null)
    : BaseShellCollector<DockerCollector>(logger, shell), IDockerCollector
{
    public async Task<DockerDataDto> CollectAllAsync(CancellationToken ct = default)
    {
        var containersTask = CollectContainersAsync(ct);
        var imagesTask = CollectImagesAsync(ct);
        var composeTask = CollectComposeStacksAsync(ct);
        var networksTask = CollectNetworksAsync(ct);
        var volumesTask = CollectVolumesAsync(ct);

        await Task.WhenAll(containersTask, imagesTask, composeTask, networksTask, volumesTask).ConfigureAwait(false);

        var containers = await containersTask.ConfigureAwait(false);

        if (containers.Count > 0)
        {
            await EnrichWithStatsAsync(containers, ct).ConfigureAwait(false);
            containers = await InspectSecurityAsync(containers, ct).ConfigureAwait(false);
        }

        return new DockerDataDto
        {
            Containers = containers,
            Images = await imagesTask.ConfigureAwait(false),
            ComposeStacks = await composeTask.ConfigureAwait(false),
            Networks = await networksTask.ConfigureAwait(false),
            Volumes = await volumesTask.ConfigureAwait(false)
        };
    }

    private async Task<List<DockerContainerDto>> CollectContainersAsync(CancellationToken ct)
    {
        var result = new List<DockerContainerDto>();
        try
        {
            var res = await Shell.RunExecAsync("docker",
                ["ps", "-a", "--format", "{{.ID}}\\t{{.Names}}\\t{{.Image}}\\t{{.State}}\\t{{.Status}}\\t{{.Ports}}\\t{{.CreatedAt}}\\t{{.Label \"com.docker.compose.project\"}}"],
                ct).ConfigureAwait(false);
            var output = res.StdOut;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 7) continue;

                var project = parts.Length >= 8 ? parts[7].Trim() : string.Empty;

                result.Add(new DockerContainerDto
                {
                    ContainerId = parts[0],
                    Name = parts[1],
                    Image = parts[2],
                    State = parts[3],
                    Status = parts[4],
                    Ports = parts[5],
                    Created = ParseDockerDate(parts[6]),
                    Project = string.IsNullOrWhiteSpace(project) ? InferProjectFromName(parts[1]) : project
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to collect Docker containers");
        }
        return result;
    }

    internal static string InferProjectFromName(string containerName)
    {
        if (string.IsNullOrWhiteSpace(containerName)) return string.Empty;
        var idx = containerName.IndexOfAny(['_', '-']);
        return idx > 0 ? containerName[..idx] : string.Empty;
    }

    private async Task EnrichWithStatsAsync(List<DockerContainerDto> containers, CancellationToken ct)
    {
        try
        {
            var res = await Shell.RunExecAsync("docker",
                ["stats", "--no-stream", "--format", "{{.ID}}\\t{{.CPUPerc}}\\t{{.MemUsage}}"],
                ct).ConfigureAwait(false);
            var output = res.StdOut;

            var statsMap = new Dictionary<string, (double Cpu, double MemUsage, double MemLimit)>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;

                var id = parts[0];
                var cpu = ParsePercent(parts[1]);
                var (used, limit) = ParseMemUsage(parts[2]);
                statsMap[id] = (cpu, used, limit);
            }

            for (var i = 0; i < containers.Count; i++)
            {
                var c = containers[i];
                if (statsMap.TryGetValue(c.ContainerId, out var stats))
                {
                    containers[i] = c with
                    {
                        CpuPercent = stats.Cpu,
                        MemoryUsageMb = stats.MemUsage,
                        MemoryLimitMb = stats.MemLimit
                    };
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to collect Docker stats");
        }
    }

    /// <summary>One read-only <c>docker inspect</c> for the listed containers. Docker answers non-zero
    /// when one of them vanished since <c>ps</c> but still prints the others, so the output is read
    /// whatever the exit code; a container missing from it stays uninspected (unknown).</summary>
    private async Task<List<DockerContainerDto>> InspectSecurityAsync(
        List<DockerContainerDto> containers, CancellationToken ct)
    {
        try
        {
            var res = await Shell.RunExecAsync("docker",
                DockerContainerSecurityInspector.BuildArguments(containers.Select(c => c.ContainerId)),
                ct).ConfigureAwait(false);
            return DockerContainerSecurityInspector.Apply(
                containers, res.StdOut, storage?.BuilderName,
                runtimeHealth is null ? null : runtimeHealth.CreatedDuringLivePipelineTask);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Failed to inspect Docker container security settings");
            return containers;
        }
    }

    private async Task<List<DockerImageDto>> CollectImagesAsync(CancellationToken ct)
    {
        var result = new List<DockerImageDto>();
        try
        {
            var res = await Shell.RunExecAsync("docker",
                ["images", "--format", "{{.ID}}\\t{{.Repository}}\\t{{.Tag}}\\t{{.Size}}\\t{{.CreatedAt}}"],
                ct).ConfigureAwait(false);
            var output = res.StdOut;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 5) continue;

                result.Add(new DockerImageDto
                {
                    ImageId = parts[0],
                    Repository = parts[1],
                    Tag = parts[2],
                    Size = parts[3],
                    Created = ParseDockerDate(parts[4])
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to collect Docker images");
        }
        return result;
    }

    private async Task<List<DockerComposeStackDto>> CollectComposeStacksAsync(CancellationToken ct)
    {
        var result = new List<DockerComposeStackDto>();
        try
        {
            var res = await Shell.RunExecAsync("docker",
                ["compose", "ls", "--format", "table"],
                ct).ConfigureAwait(false);
            var output = res.StdOut;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                var name = parts[0];
                var status = parts[1];
                var configFile = string.Join(" ", parts.Skip(2));

                // F-ENG-06 KNOWN LIMITATION: `docker compose ls` only reports the number of RUNNING services
                // in its status (e.g. "running(2)"); it does not expose the count of DEFINED services. So for a
                // partially-started stack TotalCount cannot be derived here without a per-stack `ps -a`, and it
                // is reported equal to RunningCount (a lower bound), not the true defined total.
                var running = 0;
                var total = 0;
                if (status.Contains('('))
                {
                    var numStr = status.Split('(', ')');
                    if (numStr.Length >= 2 && int.TryParse(numStr[1], out var n))
                    {
                        running = n;
                        total = n; // lower bound: `ls` does not surface the defined-service total
                    }
                }

                result.Add(new DockerComposeStackDto
                {
                    Name = name,
                    Status = status,
                    ConfigFile = configFile,
                    RunningCount = running,
                    TotalCount = total
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to collect Docker Compose stacks");
        }
        return result;
    }

    private async Task<List<DockerNetworkDto>> CollectNetworksAsync(CancellationToken ct)
    {
        var result = new List<DockerNetworkDto>();
        try
        {
            var res = await Shell.RunExecAsync("docker",
                ["network", "ls", "--format", "{{.ID}}\\t{{.Name}}\\t{{.Driver}}\\t{{.Scope}}\\t{{.Label \"com.docker.compose.project\"}}"],
                ct).ConfigureAwait(false);
            var output = res.StdOut;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 4) continue;

                result.Add(new DockerNetworkDto
                {
                    NetworkId = parts[0],
                    Name = parts[1],
                    Driver = parts[2],
                    Scope = parts[3],
                    Project = parts.Length > 4 ? parts[4].Trim() : string.Empty
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to collect Docker networks");
        }
        return result;
    }

    private async Task<List<DockerVolumeDto>> CollectVolumesAsync(CancellationToken ct)
    {
        var result = new List<DockerVolumeDto>();
        try
        {
            var res = await Shell.RunExecAsync("docker",
                ["volume", "ls", "--format", "{{.Name}}\\t{{.Driver}}\\t{{.Mountpoint}}\\t{{.Label \"com.docker.compose.project\"}}"],
                ct).ConfigureAwait(false);
            var output = res.StdOut;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;

                result.Add(new DockerVolumeDto
                {
                    Name = parts[0],
                    Driver = parts[1],
                    Mountpoint = parts[2],
                    Project = parts.Length > 3 ? parts[3].Trim() : string.Empty
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to collect Docker volumes");
        }
        return result;
    }

    internal static double ParsePercent(string raw)
    {
        var cleaned = raw.Trim().TrimEnd('%');
        return double.TryParse(cleaned, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    internal static (double UsedMb, double LimitMb) ParseMemUsage(string raw)
    {
        var parts = raw.Split('/');
        if (parts.Length < 2) return (0, 0);
        return (ParseSizeToMb(parts[0].Trim()), ParseSizeToMb(parts[1].Trim()));
    }

    internal static double ParseSizeToMb(string raw)
    {
        var upper = raw.ToUpperInvariant().Trim();
        var (suffix, multiplier) = upper switch
        {
            _ when upper.EndsWith("TIB", StringComparison.Ordinal) => ("TIB", 1024.0 * 1024),
            _ when upper.EndsWith("TB", StringComparison.Ordinal) => ("TB", 1_000_000.0),
            _ when upper.EndsWith("GIB", StringComparison.Ordinal) => ("GIB", 1024.0),
            _ when upper.EndsWith("GB", StringComparison.Ordinal) => ("GB", 1000.0),
            _ when upper.EndsWith("MIB", StringComparison.Ordinal) => ("MIB", 1.0),
            _ when upper.EndsWith("MB", StringComparison.Ordinal) => ("MB", 1.0),
            _ when upper.EndsWith("KIB", StringComparison.Ordinal) => ("KIB", 1.0 / 1024),
            _ when upper.EndsWith("KB", StringComparison.Ordinal) => ("KB", 1.0 / 1000),
            _ when upper.EndsWith('B') => ("B", 1.0 / 1_000_000),
            _ => (string.Empty, 1.0)
        };

        if (suffix.Length > 0)
            upper = upper[..^suffix.Length];

        return double.TryParse(upper.Trim(), CultureInfo.InvariantCulture, out var v) ? v * multiplier : 0;
    }

    internal static DateTime ParseDockerDate(string raw)
    {
        var trimmed = raw.Trim();
        var spaceCount = 0;
        var end = trimmed.Length;
        for (var i = trimmed.Length - 1; i >= 0; i--)
        {
            if (trimmed[i] == ' ') spaceCount++;
            if (spaceCount == 2) { end = i; break; }
        }
        var datePart = trimmed[..end];

        if (DateTime.TryParse(datePart, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return dt.ToUniversalTime();

        // audit: parse failure sentinel - surfacing a clear MinValue rather than a fake "now".
        return DateTime.MinValue;
    }
}
