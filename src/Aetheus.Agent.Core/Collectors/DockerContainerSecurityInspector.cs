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

    /// <summary>One JSON object per container. Missing labels come back as <c>null</c>.</summary>
    internal const string InspectFormat =
        "{\"id\":{{json .Id}},\"name\":{{json .Name}},\"image\":{{json .Config.Image}},"
        + "\"privileged\":{{json .HostConfig.Privileged}},\"pidMode\":{{json .HostConfig.PidMode}},"
        + "\"networkMode\":{{json .HostConfig.NetworkMode}},\"capAdd\":{{json .HostConfig.CapAdd}},"
        + "\"mounts\":{{json .Mounts}},"
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
    internal static List<DockerContainerDto> Apply(
        IReadOnlyList<DockerContainerDto> containers, string inspectOutput, string? builderName)
    {
        var findings = Parse(inspectOutput, builderName);
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

    internal static List<Finding> Parse(string inspectOutput, string? builderName)
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
                var finding = Read(document.RootElement, builderName);
                if (finding is not null) result.Add(finding);
            }
        }
        return result;
    }

    private static Finding? Read(JsonElement root, string? builderName)
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
        var role = ResolveRole(String(root, "name").TrimStart('/'), String(root, "image"),
            String(root, "role"), String(root, "service"), builderName);
        return new Finding(id, risks, capabilities, role);
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
