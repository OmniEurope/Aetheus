// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Docker;

public class DockerService(IDockerRepository repo, IAuditService audit, ITaskService taskService) : IDockerService
{
    private Task QueueTaskAsync(ServerTask task, CancellationToken ct = default)
        => TaskQueuePersistence.PersistAndNotifyAsync(repo.AddTaskAsync, taskService, task, ct);

    public async Task<List<DockerContainerDto>> GetContainersAsync(int serverId, CancellationToken ct = default)
    {
        var containers = await repo.GetContainersAsync(serverId, ct).ConfigureAwait(false);
        return ServerDataMapper.MapDockerContainers(containers);
    }

    public async Task ExecuteActionAsync(int serverId, DockerActionRequest request, CancellationToken ct = default)
    {
        if (!DockerIdValidator.IsValidContainerId(request.ContainerId))
            throw new BadRequestException("Invalid container ID.");

        await QueueTaskAsync(TypedOperation(
            serverId, $"Docker {request.Action} - {request.ContainerId[..Math.Min(12, request.ContainerId.Length)]}",
            MapActionToOperation(request.Action), request.ContainerId, 30), ct).ConfigureAwait(false);
        await audit.LogAsync($"Docker{request.Action}", "Container", serverId, request.ContainerId, ct).ConfigureAwait(false);
    }

    public async Task<string> GetContainerLogsAsync(int serverId, DockerContainerLogsRequest request, CancellationToken ct = default)
    {
        if (!DockerIdValidator.IsValidContainerId(request.ContainerId)) return string.Empty;

        var tail = Math.Clamp(request.Tail, 1, 10000);
        var command = $"docker logs --tail {tail} {request.ContainerId}";

        await QueueTaskAsync(ServerTaskFactory.Shell(serverId, $"Docker logs - {request.ContainerId[..Math.Min(12, request.ContainerId.Length)]}", command, 15), ct).ConfigureAwait(false);
        return "Log retrieval task queued.";
    }

    public async Task<List<DockerImageDto>> GetImagesAsync(int serverId, CancellationToken ct = default)
    {
        var images = await repo.GetImagesAsync(serverId, ct).ConfigureAwait(false);
        return images.Select(i => new DockerImageDto
        {
            ImageId = i.ImageId,
            Repository = i.Repository,
            Tag = i.Tag,
            Size = i.Size,
            Created = i.Created
        }).ToList();
    }

    public async Task PullImageAsync(int serverId, DockerPullImageRequest request, CancellationToken ct = default)
    {
        if (!DockerIdValidator.IsValidImageName(request.Image))
            throw new BadRequestException("Invalid image name.");

        await QueueTaskAsync(TypedOperation(
            serverId, $"Docker pull - {request.Image}", OperationKind.DockerPullImage, request.Image, 300), ct).ConfigureAwait(false);
    }

    public async Task RemoveImageAsync(int serverId, string imageId, CancellationToken ct = default)
    {
        if (!DockerIdValidator.IsValidImageId(imageId))
            throw new BadRequestException("Invalid image ID.");

        await QueueTaskAsync(TypedOperation(
            serverId, $"Docker rmi - {imageId[..Math.Min(12, imageId.Length)]}", OperationKind.DockerRemoveImage, imageId, 30), ct).ConfigureAwait(false);
    }

    public async Task<List<DockerComposeStackDto>> GetComposeStacksAsync(int serverId, CancellationToken ct = default)
    {
        var stacks = await repo.GetComposeStacksAsync(serverId, ct).ConfigureAwait(false);
        return stacks.Select(s => new DockerComposeStackDto
        {
            Name = s.Name,
            Status = s.Status,
            ConfigFile = s.ConfigFile,
            RunningCount = s.RunningCount,
            TotalCount = s.TotalCount
        }).ToList();
    }

    public async Task ExecuteComposeActionAsync(int serverId, DockerComposeActionRequest request, CancellationToken ct = default)
    {
        if (!DockerIdValidator.IsValidName(request.StackName))
            throw new BadRequestException("Invalid stack name.");

        var operation = request.Action switch
        {
            DockerComposeAction.Up => OperationKind.DockerComposeProjectUp,
            DockerComposeAction.Down => OperationKind.DockerComposeProjectDown,
            DockerComposeAction.Restart => OperationKind.DockerComposeProjectRestart,
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };

        await QueueTaskAsync(TypedOperation(
            serverId, $"Compose {request.Action} - {request.StackName}", operation, request.StackName, 120), ct).ConfigureAwait(false);
    }

    public async Task<List<DockerNetworkDto>> GetNetworksAsync(int serverId, CancellationToken ct = default)
    {
        var networks = await repo.GetNetworksAsync(serverId, ct).ConfigureAwait(false);
        return networks.Select(n => new DockerNetworkDto
        {
            NetworkId = n.NetworkId,
            Name = n.Name,
            Driver = n.Driver,
            Scope = n.Scope
        }).ToList();
    }

    public async Task<List<DockerVolumeDto>> GetVolumesAsync(int serverId, CancellationToken ct = default)
    {
        var volumes = await repo.GetVolumesAsync(serverId, ct).ConfigureAwait(false);
        return volumes.Select(v => new DockerVolumeDto
        {
            Name = v.Name,
            Driver = v.Driver,
            Mountpoint = v.Mountpoint
        }).ToList();
    }

    public async Task PruneAsync(int serverId, DockerPruneRequest request, CancellationToken ct = default)
    {
        // The agent runs one fixed prune command per target, in this order, stopping at the first failure.
        var targets = new List<string>();
        if (request.Containers) targets.Add("containers");
        if (request.Images) targets.Add("images");
        if (request.Volumes) targets.Add("volumes");
        if (targets.Count == 0)
            throw new BadRequestException("No prune targets specified.");

        await QueueTaskAsync(TypedOperation(
            serverId, "Docker prune", OperationKind.DockerPrune, string.Join(',', targets), 120), ct).ConfigureAwait(false);
    }

    public async Task UpdateResourceLimitsAsync(int serverId, DockerResourceLimitsRequest request, CancellationToken ct = default)
    {
        if (!DockerIdValidator.IsValidContainerId(request.ContainerId))
            throw new BadRequestException("Invalid container ID.");

        var limits = new Dictionary<string, string>();
        if (request.CpuLimit > 0) limits["AETHEUS_DOCKER_CPUS"] = request.CpuLimit.ToString("F2", CultureInfo.InvariantCulture);
        if (request.MemoryLimitMb > 0) limits["AETHEUS_DOCKER_MEMORY_MB"] = request.MemoryLimitMb.ToString(CultureInfo.InvariantCulture);
        if (limits.Count == 0)
            throw new BadRequestException("No resource limits specified.");

        await QueueTaskAsync(TypedOperation(
            serverId, $"Docker update limits - {request.ContainerId[..Math.Min(12, request.ContainerId.Length)]}",
            OperationKind.DockerUpdateResources, request.ContainerId, 15, limits), ct).ConfigureAwait(false);
    }

    private static OperationKind MapActionToOperation(DockerContainerAction action) => action switch
    {
        DockerContainerAction.Start => OperationKind.DockerStartContainer,
        DockerContainerAction.Stop => OperationKind.DockerStopContainer,
        DockerContainerAction.Restart => OperationKind.DockerRestartContainer,
        DockerContainerAction.Remove => OperationKind.DockerRemoveContainer,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    // R-119: a change the server's Docker page asks for is a typed operation, never a docker command line built from
    // the request. Its kind tells the agent that its cached Docker inventory is outdated, so the agent collects it
    // again and sends a heartbeat at once, and the page's task veil lifts on the new state (task 11784: a container
    // stopped by a shell task was still shown running). The target is validated by the agent with the same rule.
    private static ServerTask TypedOperation(
        int serverId, string name, OperationKind operation, string target, int timeoutSeconds,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        if (!OperationTargetValidator.IsValid(operation, target))
            throw new BadRequestException("Invalid docker target.");
        return environmentVariables is null
            ? ServerTaskFactory.Operation(serverId, name, operation, target, timeoutSeconds)
            : ServerTaskFactory.Operation(serverId, name, operation, target, environmentVariables, timeoutSeconds);
    }

    public async Task InspectContainerAsync(int serverId, string containerId, CancellationToken ct = default)
    {
        if (!DockerIdValidator.IsValidContainerId(containerId))
            throw new BadRequestException("Invalid container ID.");

        await QueueTaskAsync(ServerTaskFactory.Shell(serverId, $"Docker inspect - {containerId[..Math.Min(12, containerId.Length)]}", $"docker inspect {containerId}", 15), ct).ConfigureAwait(false);
    }

    public async Task GetComposeFileAsync(int serverId, string stackName, CancellationToken ct = default)
    {
        if (!DockerIdValidator.IsValidName(stackName))
            throw new BadRequestException("Invalid stack name.");

        await QueueTaskAsync(ServerTaskFactory.Shell(serverId, DockerTaskNames.ComposeFile(stackName), $"docker compose -p {stackName} config", 15), ct).ConfigureAwait(false);
    }

    public async Task SaveComposeFileAsync(int serverId, DockerComposeFileSaveRequest request, CancellationToken ct = default)
    {
        if (!DockerIdValidator.IsValidName(request.StackName))
            throw new BadRequestException("Invalid stack name.");
        if (string.IsNullOrWhiteSpace(request.Content))
            throw new BadRequestException("Content is required.");

        await QueueTaskAsync(PayloadOperation(
            serverId, $"Docker compose deploy - {request.StackName}", OperationKind.DockerComposeUp,
            request.StackName, "AETHEUS_DOCKER_COMPOSE_B64", request.Content, 120), ct).ConfigureAwait(false);
    }

    public async Task ExecuteShellCommandAsync(int serverId, DockerExecRequest request, CancellationToken ct = default)
    {
        if (!DockerIdValidator.IsValidContainerId(request.ContainerId))
            throw new BadRequestException("Invalid container ID.");
        if (string.IsNullOrWhiteSpace(request.Command))
            throw new BadRequestException("Command is required.");

        await QueueTaskAsync(PayloadOperation(
            serverId, $"Docker exec - {request.ContainerId[..Math.Min(12, request.ContainerId.Length)]}",
            OperationKind.DockerExecScript, request.ContainerId, "AETHEUS_DOCKER_EXEC_B64", request.Command, 30), ct).ConfigureAwait(false);
        await audit.LogAsync("DockerExec", "Server", serverId, request.ContainerId[..Math.Min(12, request.ContainerId.Length)], ct).ConfigureAwait(false);
    }

    public async Task GetContainerEnvVarsAsync(int serverId, string containerId, CancellationToken ct = default)
    {
        if (!DockerIdValidator.IsValidContainerId(containerId))
            throw new BadRequestException("Invalid container ID.");

        await QueueTaskAsync(ServerTaskFactory.Shell(serverId, $"Docker env - {containerId[..Math.Min(12, containerId.Length)]}", $"docker inspect --format='{{{{json .Config.Env}}}}' {containerId}", 15), ct).ConfigureAwait(false);
    }

    public async Task ListContainerFilesAsync(int serverId, DockerBrowseRequest request, CancellationToken ct = default)
    {
        if (!DockerIdValidator.IsValidContainerId(request.ContainerId))
            throw new BadRequestException("Invalid container ID.");
        if (!DockerIdValidator.IsValidContainerPath(request.Path))
            throw new BadRequestException("Invalid container path.");

        // Hardening (High #9): pass path as a positional argument after `--` so it can never be
        // interpreted as an `ls` option, and avoid wrapping the whole thing in `sh -c`.
        var command = $"docker exec {request.ContainerId} ls -la -- {request.Path}";

        await QueueTaskAsync(ServerTaskFactory.Shell(serverId, $"Docker ls - {request.ContainerId[..Math.Min(12, request.ContainerId.Length)]}", command, 15), ct).ConfigureAwait(false);
    }

    public async Task BuildImageAsync(int serverId, DockerBuildRequest request, CancellationToken ct = default)
    {
        if (!DockerIdValidator.IsValidImageName(request.ImageTag))
            throw new BadRequestException("Invalid image tag.");
        if (string.IsNullOrWhiteSpace(request.DockerfileContent))
            throw new BadRequestException("Dockerfile content is required.");

        await QueueTaskAsync(PayloadOperation(
            serverId, $"Docker build - {request.ImageTag}", OperationKind.DockerBuildImage,
            request.ImageTag, "AETHEUS_DOCKER_DOCKERFILE_B64", request.DockerfileContent, 600), ct).ConfigureAwait(false);
    }

    // BACK-012: a user payload (Compose file, exec script, Dockerfile) travels base64-encoded in an
    // environment variable of a typed operation. The former `printf | base64 -d | ...` shell strings
    // were refused by the agent's command validator before running, so these features never worked;
    // the validator itself is unchanged.
    private static ServerTask PayloadOperation(
        int serverId, string name, OperationKind operation, string target,
        string payloadVariable, string payload, int timeoutSeconds)
        => TypedOperation(
            serverId, name, operation, target, timeoutSeconds,
            new Dictionary<string, string>
            {
                [payloadVariable] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload))
            });
}
