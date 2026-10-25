// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>
/// Reads the host-escape settings of the listed containers (privileged, Docker socket mount, host PID or
/// network namespace, dangerous added capabilities) with one <c>docker inspect</c> through the Docker
/// access the agent already has. The format projects only those fields: environment variables and other
/// configuration never reach the agent's memory. A container absent from the answer stays uninspected,
/// which the interface shows as unknown, never as clean.
/// </summary>
internal static class DockerContainerSecurityInspector
{
    internal const string RoleLabel = "io.aetheus.role";
    internal const string BrowserSmokeRole = "browser-smoke";
    internal const string TestcontainersLabel = "org.testcontainers";
    internal const string RyukImageRepository = "testcontainers/ryuk";
    internal const string RyukNamePrefix = "testcontainers-ryuk-";
    /// <summary>The only process the testcontainers/ryuk image starts: its Dockerfile declares
    /// <c>CMD ["/bin/ryuk"]</c> and no entrypoint, and Testcontainers configures it through environment
    /// variables, never arguments.</summary>
    internal const string RyukProcessPath = "/bin/ryuk";

    /// <summary>One JSON object per container. Missing labels come back as <c>null</c>.</summary>
    internal const string InspectFormat =
        "{\"id\":{{json .Id}},\"name\":{{json .Name}},\"image\":{{json .Config.Image}},"
        + "\"privileged\":{{json .HostConfig.Privileged}},\"pidMode\":{{json .HostConfig.PidMode}},"
        + "\"networkMode\":{{json .HostConfig.NetworkMode}},\"capAdd\":{{json .HostConfig.CapAdd}},"
        + "\"mounts\":{{json .Mounts}},"
        + "\"created\":{{json .Created}},\"path\":{{json .Path}},\"args\":{{json .Args}},"
        + "\"testcontainers\":{{json (index .Config.Labels \"" + TestcontainersLabel + "\")}},"
        + "\"role\":{{json (index .Config.Labels \"" + RoleLabel + "\")}},"
        + "\"service\":{{json (index .Config.Labels \"com.docker.compose.service\")}}}";

    private static readonly HashSet<string> SocketSources = new(StringComparer.Ordinal)
    {
        "/var/run/docker.sock", "/run/docker.sock", "/var/run", "/run"
    };

    private static readonly HashSet<string> VpsSimulatorServices = new(StringComparer.Ordinal)
    {
        "vpssim", "vpssim-blank"
    };

    internal static IReadOnlyList<string> BuildArguments(IEnumerable<string> containerIds) =>
        ["inspect", "--type", "container", "--format", InspectFormat, .. containerIds];

    /// <summary>Applies the inspect answer to the containers it describes, matched by ID prefix
    /// (<c>docker ps</c> prints the 12-character short ID).</summary>
    /// <param name="createdDuringPipelineTask">The agent's own record of its pipeline tasks (R-33): true when a
    /// container created at that instant belongs to a run still live. Null recognises no Testcontainers
    /// reaper.</param>
    internal static List<DockerContainerDto> Apply(
        IReadOnlyList<DockerContainerDto> containers, string inspectOutput, string? builderName,
        Func<DateTimeOffset, bool>? createdDuringPipelineTask = null)
    {
        var findings = Parse(inspectOutput, builderName, createdDuringPipelineTask);
        return containers.Select(container =>
        {
            var match = findings.FirstOrDefault(f =>
                container.ContainerId.Length > 0
                && f.Id.StartsWith(container.ContainerId, StringComparison.OrdinalIgnoreCase));
            return match is null
                ? container
                : container with
                {
                    SecurityInspected = true,
                    Risks = match.Risks,
                    DangerousCapabilities = match.DangerousCapabilities,
                    AetheusRole = match.Role
                };
        }).ToList();
    }

    internal sealed record Finding(
        string Id, DockerContainerRisks Risks, string DangerousCapabilities, AetheusContainerRole Role);

    internal static List<Finding> Parse(
        string inspectOutput, string? builderName, Func<DateTimeOffset, bool>? createdDuringPipelineTask = null)
    {
        var result = new List<Finding>();
        foreach (var line in inspectOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // One unreadable line leaves that container uninspected (unknown), not clean.
                continue;
            }
            using (document)
            {
                var finding = Read(document.RootElement, builderName, createdDuringPipelineTask);
                if (finding is not null) result.Add(finding);
            }
        }
        return result;
    }

    private static Finding? Read(
        JsonElement root, string? builderName, Func<DateTimeOffset, bool>? createdDuringPipelineTask)
    {
        var id = String(root, "id");
        if (id.Length == 0) return null;
        var risks = DockerContainerRisks.None;
        if (root.TryGetProperty("privileged", out var privileged) && privileged.ValueKind == JsonValueKind.True)
            risks |= DockerContainerRisks.Privileged;
        if (string.Equals(String(root, "pidMode"), "host", StringComparison.Ordinal))
            risks |= DockerContainerRisks.HostPid;
        if (string.Equals(String(root, "networkMode"), "host", StringComparison.Ordinal))
            risks |= DockerContainerRisks.HostNetwork;
        if (MountsDockerSocket(root))
            risks |= DockerContainerRisks.DockerSocket;
        var capabilities = DangerousCapabilities(root);
        if (capabilities.Length > 0)
            risks |= DockerContainerRisks.DangerousCapabilities;
        var name = String(root, "name").TrimStart('/');
        var image = String(root, "image");
        var role = ResolveRole(name, image, String(root, "role"), String(root, "service"), builderName);
        if (role == AetheusContainerRole.None
            && IsPipelineTestcontainersReaper(name, image, String(root, "testcontainers"), String(root, "created"),
                RunsOnlyTheRyukProcess(root), createdDuringPipelineTask))
            role = AetheusContainerRole.TestcontainersReaper;
        return new Finding(id, risks, capabilities, role);
    }

    /// <summary>
    /// R-33: Ryuk, started by a pipeline step's tests. Every condition is required: the image repository is
    /// exactly <c>testcontainers/ryuk</c> (any tag or digest, no other registry or prefix), the container
    /// carries <c>org.testcontainers=true</c>, its name is <c>testcontainers-ryuk-</c> followed by a session
    /// UUID, its process is the image's own <see cref="RyukProcessPath"/> with no arguments (an overridden
    /// entrypoint or command runs arbitrary code with Ryuk's privileges), and the agent's own task record
    /// places its creation inside a pipeline task still live (or just ended). The name, image and label alone
    /// are chosen by whoever starts the container; the task record is not, so a look-alike started outside a
    /// run, or one that outlives its run, stays flagged.
    /// </summary>
    internal static bool IsPipelineTestcontainersReaper(
        string name, string image, string testcontainersLabel, string created, bool runsOnlyTheRyukProcess,
        Func<DateTimeOffset, bool>? createdDuringPipelineTask)
    {
        if (createdDuringPipelineTask is null) return false;
        if (!runsOnlyTheRyukProcess) return false;
        if (!string.Equals(ImageRepository(image), RyukImageRepository, StringComparison.Ordinal)) return false;
        if (!string.Equals(testcontainersLabel, "true", StringComparison.Ordinal)) return false;
        if (!name.StartsWith(RyukNamePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[RyukNamePrefix.Length..], "D", out _)) return false;
        return DateTimeOffset.TryParse(created, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var createdAt)
            && createdDuringPipelineTask(createdAt);
    }

    /// <summary>True only when Docker reports the container's process as exactly <see cref="RyukProcessPath"/>
    /// with an empty argument list; a missing or malformed path or argument list counts as another process.</summary>
    private static bool RunsOnlyTheRyukProcess(JsonElement root) =>
        string.Equals(String(root, "path"), RyukProcessPath, StringComparison.Ordinal)
        && root.TryGetProperty("args", out var args)
        && args.ValueKind == JsonValueKind.Array
        && args.GetArrayLength() == 0;

    /// <summary>The repository of an image reference, without tag or digest; <c>docker.io/</c> is how Docker
    /// Hub may be written, any other registry is a different image.</summary>
    private static string ImageRepository(string image)
    {
        var reference = image.Trim();
        var digest = reference.IndexOf('@', StringComparison.Ordinal);
        if (digest >= 0) reference = reference[..digest];
        var tag = reference.LastIndexOf(':');
        if (tag > reference.LastIndexOf('/')) reference = reference[..tag];
        return reference.StartsWith("docker.io/", StringComparison.Ordinal) ? reference["docker.io/".Length..] : reference;
    }

    private static bool MountsDockerSocket(JsonElement root)
    {
        if (!root.TryGetProperty("mounts", out var mounts) || mounts.ValueKind != JsonValueKind.Array) return false;
        foreach (var mount in mounts.EnumerateArray())
        {
            if (mount.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(String(mount, "Type"), "bind", StringComparison.Ordinal)) continue;
            var source = String(mount, "Source").TrimEnd('/');
            if (SocketSources.Contains(source)) return true;
        }
        return false;
    }

    private static string DangerousCapabilities(JsonElement root)
    {
        if (!root.TryGetProperty("capAdd", out var capAdd) || capAdd.ValueKind != JsonValueKind.Array) return string.Empty;
        var found = capAdd.EnumerateArray()
            .Where(cap => cap.ValueKind == JsonValueKind.String)
            .Select(cap => Normalize(cap.GetString()!))
            .Where(cap => DockerContainerSecurity.DangerousCapabilityNames.Contains(cap, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return string.Join(",", found);
    }

    private static string Normalize(string capability)
    {
        var upper = capability.Trim().ToUpperInvariant();
        return upper.StartsWith("CAP_", StringComparison.Ordinal) ? upper[4..] : upper;
    }

    /// <summary>Names the containers Aetheus starts itself. Recognition never hides a flag: only the
    /// flags of <see cref="DockerContainerSecurity.ExpectedRisks"/> are considered explained.</summary>
    internal static AetheusContainerRole ResolveRole(
        string name, string image, string roleLabel, string composeService, string? builderName)
    {
        if (!string.IsNullOrWhiteSpace(builderName)
            && image.StartsWith("moby/buildkit", StringComparison.Ordinal)
            && Regex.IsMatch(name, "^buildx_buildkit_" + Regex.Escape(builderName) + "[0-9]+$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return AetheusContainerRole.BuildKitBuilder;
        if (string.Equals(roleLabel, BrowserSmokeRole, StringComparison.Ordinal))
            return AetheusContainerRole.BrowserSmoke;
        if (VpsSimulatorServices.Contains(composeService) && name.EndsWith("-vpssim", StringComparison.Ordinal))
            return AetheusContainerRole.VpsSimulator;
        return AetheusContainerRole.None;
    }

    private static string String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
