// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Drift guard: <see cref="DeployOperationExecutor.DeployBaseDir"/> and
/// <see cref="DeployOperationExecutor.RestartHelperPath"/> are hard-coded in the agent AND in
/// <c>deploy/agent-installer/install-agent-linux.sh</c> (the script creates the dirs/helper; the agent writes
/// releases there and shells out to the helper). If they drift, every deploy fails at runtime - there
/// is no compile-time link. This test resolves the script's shell variables and asserts equality.
/// </summary>
public sealed class DeployPathConstantsAuditTests
{
    [Fact]
    public void DeploymentModule_ProvisionsDatabaseRestoreClients()
    {
        var script = ReadInstallScript();

        Assert.Contains("ensure_backup_restore_packages", script, StringComparison.Ordinal);
        Assert.Contains("ask_install_package \"pg_restore\" \"postgresql-client\"", script, StringComparison.Ordinal);
        Assert.Contains("ask_install_package \"mysql\" \"default-mysql-client\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployBaseDir_matches_install_script()
    {
        var script = ReadInstallScript();
        var workDir = ResolveVar(script, "WORK_DIR");
        var deployBaseDir = ResolveVar(script, "DEPLOY_BASE_DIR").Replace("$WORK_DIR", workDir, StringComparison.Ordinal);

        Assert.Equal(DeployOperationExecutor.DeployBaseDir, deployBaseDir);
    }

    [Fact]
    public void RestartHelperPath_matches_install_script()
    {
        var script = ReadInstallScript();
        var helperDir = ResolveVar(script, "AETHEUS_HELPER_DIR");
        var helperPath = ResolveVar(script, "DEPLOY_RESTART_HELPER_PATH").Replace("$AETHEUS_HELPER_DIR", helperDir, StringComparison.Ordinal);

        Assert.Equal(DeployOperationExecutor.RestartHelperPath, helperPath);
    }

    [Fact]
    public void UnitTemplateName_matches_install_script()
    {
        // The agent restarts aetheus-app@<app>.service; the script poses the matching template at
        // DEPLOY_UNIT_TEMPLATE_PATH=/etc/systemd/system/aetheus-app@.service. A rename on either
        // side breaks the health-gate at deploy time, not at build time - so pin them together.
        var script = ReadInstallScript();
        var templatePath = ResolveVar(script, "DEPLOY_UNIT_TEMPLATE_PATH");
        var expected = $"/etc/systemd/system/{DeployOperationExecutor.UnitTemplatePrefix}.service";

        Assert.Equal(expected, templatePath);
    }

    [Fact]
    public void Sudoers_AETHEUS_DEPLOY_points_at_RestartHelperPath()
    {
        // The aetheus-deploy sudoers drop-in must grant NOPASSWD on EXACTLY the helper path the
        // agent invokes via `sudo -n <RestartHelperPath> <app>`; a path drift makes sudo reject the
        // restart at runtime. Assert the helper path appears in the sudoers heredoc.
        var script = ReadInstallScript();
        var helperDir = ResolveVar(script, "AETHEUS_HELPER_DIR");
        var helperPath = ResolveVar(script, "DEPLOY_RESTART_HELPER_PATH").Replace("$AETHEUS_HELPER_DIR", helperDir, StringComparison.Ordinal);

        Assert.Equal(DeployOperationExecutor.RestartHelperPath, helperPath);
        // The drop-in is the deploy/sudoers.d/aetheus-deploy template the installer renders.
        Assert.Contains(
            "render_host_config deploy/sudoers.d/aetheus-deploy \"$DEPLOY_MANAGE_SUDOERS_FILE\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Cmnd_Alias AETHEUS_DEPLOY = #{DEPLOY_RESTART_HELPER_PATH}#",
            LinuxHostConfigTemplates.Read("deploy/sudoers.d/aetheus-deploy"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentModule_grants_only_traverse_access_to_private_state_parent()
    {
        var script = ReadInstallScript();

        Assert.Contains("setfacl -m \"g:$DEPLOY_READ_GROUP:--x\" \"$WORK_DIR\"", script, StringComparison.Ordinal);
        Assert.Contains("STATE_DIRECTORY_MODE=\"0700\"", script, StringComparison.Ordinal);
        Assert.Contains("STATE_DIRECTORY_MODE=\"0710\"", script, StringComparison.Ordinal);
        // The unit is the agent/aetheus-agent.service template the installer renders.
        Assert.Contains(
            "StateDirectoryMode=#{STATE_DIRECTORY_MODE}#",
            LinuxHostConfigTemplates.Read("agent/aetheus-agent.service"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("chmod o+x \"$WORK_DIR\"", script, StringComparison.Ordinal);
    }

    // Read a `NAME="value"` assignment (first occurrence) from the script.
    private static string ResolveVar(string script, string name)
    {
        var m = Regex.Match(script, $@"^{Regex.Escape(name)}=""([^""]*)""", RegexOptions.Multiline);
        Assert.True(m.Success, $"{name}=\"…\" not found in install-agent-linux.sh");
        return m.Groups[1].Value;
    }

    private static string ReadInstallScript()
        => File.ReadAllText(Path.Combine(FindRepoRoot(), "deploy", "agent-installer", "install-agent-linux.sh"));

    private static string FindRepoRoot() => Aetheus.Agent.Core.Tests.RepositoryScan.Root;
}
