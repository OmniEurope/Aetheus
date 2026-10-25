// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Tests.Architecture;

public sealed class TeamspeakChecksumAuditTests
{
    [Fact]
    public void LinuxInstaller_FailsClosedWhenTeamspeakChecksumIsMissing()
    {
        // The helper is the teamspeak/teamspeak-setup template the installer renders.
        Assert.Contains(
            "render_host_config teamspeak/teamspeak-setup \"$TEAMSPEAK_SETUP_HELPER_PATH\"",
            File.ReadAllText(Path.Combine(FindRepoRoot(), "deploy", "agent-installer", "install-agent-linux.sh")),
            StringComparison.Ordinal);
        var script = LinuxHostConfigTemplates.Read("teamspeak/teamspeak-setup");

        Assert.Contains("TS_CHECKSUM_FILE=\"/etc/aetheus-agent/teamspeak.sha256\"", script, StringComparison.Ordinal);
        Assert.Contains("TS_SHA256=\"$(tr -d '[:space:]' < \"$TS_CHECKSUM_FILE\")\"", script, StringComparison.Ordinal);
        Assert.Contains("[ \"${#TS_SHA256}\" -eq 64 ] || die", script, StringComparison.Ordinal);
        Assert.Contains("sha256sum -c - || die", script, StringComparison.Ordinal);
        Assert.DoesNotContain("installing TeamSpeak $TS_VERSION without checksum verification", script, StringComparison.Ordinal);
    }

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;
}
