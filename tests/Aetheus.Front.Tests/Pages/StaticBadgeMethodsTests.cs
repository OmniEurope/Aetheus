// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Alerts;
using Aetheus.Front.Components.Audit;
using Aetheus.Front.Components.Plugins;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using PipelinesComp = Aetheus.Front.Components.Shared.PipelineHelper;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Tests for private/internal static badge/style methods across page components.
/// These are tested via reflection for private methods.
/// </summary>
public class StaticBadgeMethodsTests
{
    private static object? InvokePrivateStatic(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        return method.Invoke(null, args);
    }

    // --- Alerts.GetMetricBadgeStyle ---

    [Theory]
    [InlineData(MetricType.Cpu, OmniTone.Warning)]
    [InlineData(MetricType.Memory, OmniTone.Accent)]
    [InlineData(MetricType.Disk, OmniTone.Danger)]
    public void Alerts_GetMetricBadgeStyle(MetricType metric, OmniTone expected)
    {
        var result = InvokePrivateStatic(typeof(Alerts), "GetMetricBadgeStyle", metric);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(AlertSeverity.Critical, OmniTone.Danger)]
    [InlineData(AlertSeverity.Warning, OmniTone.Warning)]
    [InlineData(AlertSeverity.Info, OmniTone.Accent)]
    public void Alerts_GetSeverityBadgeStyle(AlertSeverity severity, OmniTone expected)
    {
        var result = InvokePrivateStatic(typeof(Alerts), "GetSeverityBadgeStyle", severity);
        Assert.Equal(expected, result);
    }

    // AuditActionPresentation.Badge is shared by audit lists, dialogs and entity trails.

    [Theory]
    [InlineData("Created", OmniTone.Success)]
    [InlineData("Updated", OmniTone.Accent)]
    [InlineData("Deleted", OmniTone.Danger)]
    [InlineData("Unknown", OmniTone.Neutral)]
    public void AuditAction_Badge(string action, OmniTone expected)
    {
        Assert.Equal(expected, AuditActionPresentation.Badge(action));
    }

    // --- PipelinesComp.GetTriggerIcon ---
    // Source: Webhook => "webhook", Schedule => "schedule", _ => "touch_app"

    [Theory]
    [InlineData(PipelineTriggerType.Manual, "touch_app")]
    [InlineData(PipelineTriggerType.Schedule, "schedule")]
    [InlineData(PipelineTriggerType.Webhook, "webhook")]
    public void Pipelines_GetTriggerIcon(PipelineTriggerType type, string expected)
    {
        var result = PipelinesComp.GetTriggerIcon(type);
        Assert.Equal(expected, result);
    }

    // --- PluginManagement.GetStatusBadgeStyle ---
    // Source: Enabled => Success, Disabled => Warning, Error => Danger, _ => Info

    [Theory]
    [InlineData(PluginStatus.Enabled, OmniTone.Success)]
    [InlineData(PluginStatus.Disabled, OmniTone.Warning)]
    [InlineData(PluginStatus.Error, OmniTone.Danger)]
    public void PluginManagement_GetStatusBadgeStyle(PluginStatus status, OmniTone expected)
    {
        var result = InvokePrivateStatic(typeof(Aetheus.Front.Components.Plugins.PluginManagement), "GetStatusBadgeStyle", status);
        Assert.Equal(expected, result);
    }

    // --- ServerDockerSection.GetContainerBadge ---

    [Theory]
    [InlineData("running", OmniTone.Success)]
    [InlineData("exited", OmniTone.Danger)]
    [InlineData("paused", OmniTone.Warning)]
    [InlineData("restarting", OmniTone.Accent)]
    [InlineData("created", OmniTone.Neutral)]
    public void ServerDockerSection_GetContainerBadge(string state, OmniTone expected)
    {
        var result = InvokePrivateStatic(typeof(DockerContainersTab), "GetContainerBadge", state);
        Assert.Equal(expected, result);
    }

    // --- DockerContainersTab.GetProjectAccentClass ---
    // Source: null => docker-project-accent-none, non-null => $"docker-project-accent-{hash % 8}"

    [Fact]
    public void ServerDockerSection_GetProjectAccentClass_Null()
    {
        var result = DockerContainersTab.GetProjectAccentClass(null);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ServerDockerSection_GetProjectAccentClass_NonNull()
    {
        var result = DockerContainersTab.GetProjectAccentClass("my-project");
        Assert.StartsWith("docker-project-accent-", result);
    }

    // --- ServerRkhunterSection.GetScanBadgeStyle ---
    // Source: "clean" => Success, "warning" => Warning, _ => Secondary

    [Theory]
    [InlineData("clean", OmniTone.Success)]
    [InlineData("warning", OmniTone.Warning)]
    [InlineData("error", OmniTone.Neutral)]
    public void ServerRkhunterSection_GetScanBadgeStyle(string status, OmniTone expected)
    {
        var result = InvokePrivateStatic(typeof(ServerRkhunterSection), "GetScanBadgeStyle", status);
        Assert.Equal(expected, result);
    }

    // --- ServerModulesSection.GetModuleStatusBadge ---

    [Theory]
    [InlineData(ServerModuleStatus.Active, OmniTone.Success)]
    [InlineData(ServerModuleStatus.Inactive, OmniTone.Neutral)]
    [InlineData(ServerModuleStatus.Error, OmniTone.Danger)]
    public void ServerModulesSection_GetModuleStatusBadge(ServerModuleStatus status, OmniTone expected)
    {
        var result = InvokePrivateStatic(typeof(ServerModulesSection), "GetModuleStatusBadge", status);
        Assert.Equal(expected, result);
    }

    // --- ManageableServiceGrid.FormatType ---
    // Source: Systemd => "Systemd", Docker => "Docker", WindowsService => "Windows", _ => type.ToString()

    [Theory]
    [InlineData(ServiceType.Systemd, "Systemd")]
    [InlineData(ServiceType.Docker, "Docker")]
    [InlineData(ServiceType.WindowsService, "Windows")]
    public void ServerServicesSection_FormatType(ServiceType type, string expected)
    {
        var result = ManageableServiceGrid.FormatType(type);
        Assert.Equal(expected, result);
    }

    // --- ServerServicesSection.HasDedicatedModule ---

    [Theory]
    [InlineData("apache2", true)]
    [InlineData("postfix", true)]
    [InlineData("dovecot", true)]
    [InlineData("nginx", false)]
    [InlineData("sshd", false)]
    public void ServerServicesSection_HasDedicatedModule(string serviceName, bool expected)
    {
        Assert.Equal(expected, ServerServicesSection.HasDedicatedModule(serviceName));
    }

    // --- ServerConfigSection.GetChangeBadgeStyle ---
    // Source: "pull"|"create"|"deploy"|"enable" => Success, "update" => Info, "unchanged" => Light, _ => Secondary

    [Theory]
    [InlineData("create", OmniTone.Success)]
    [InlineData("pull", OmniTone.Success)]
    [InlineData("deploy", OmniTone.Success)]
    [InlineData("enable", OmniTone.Success)]
    [InlineData("update", OmniTone.Accent)]
    [InlineData("unchanged", OmniTone.Neutral)]
    [InlineData("other", OmniTone.Neutral)]
    public void ServerConfigSection_GetChangeBadgeStyle(string action, OmniTone expected)
    {
        var result = InvokePrivateStatic(typeof(ServerConfigSection), "GetChangeBadgeStyle", action);
        Assert.Equal(expected, result);
    }

    // --- ServerAppsSection.GetAppStatusBadge ---
    // Source: Running => Success, Stopped => Light, Error => Danger, _ => Warning

    [Theory]
    [InlineData(ServerAppStatus.Running, OmniTone.Success)]
    [InlineData(ServerAppStatus.Stopped, OmniTone.Neutral)]
    [InlineData(ServerAppStatus.Error, OmniTone.Danger)]
    public void ServerAppsSection_GetAppStatusBadge(ServerAppStatus status, OmniTone expected)
    {
        var result = InvokePrivateStatic(typeof(ServerAppsSection), "GetAppStatusBadge", status);
        Assert.Equal(expected, result);
    }

    // --- ServerApacheSection.GetStatusBadge ---

    [Theory]
    [InlineData(true, OmniTone.Success)]
    [InlineData(false, OmniTone.Danger)]
    public void ServerApacheSection_GetStatusBadge(bool isRunning, OmniTone expected)
    {
        var result = InvokePrivateStatic(typeof(ServerApacheSection), "GetStatusBadge", isRunning);
        Assert.Equal(expected, result);
    }

    // --- ModuleLinksTab.GetTypeBadge ---
    // Source: Docker => Primary, Apache => Warning, Certbot => Success, _ => Light

    [Theory]
    [InlineData(ModuleLinkType.Docker, OmniTone.Accent)]
    [InlineData(ModuleLinkType.Apache, OmniTone.Warning)]
    [InlineData(ModuleLinkType.Certbot, OmniTone.Success)]
    public void ModuleLinksTab_GetTypeBadge(ModuleLinkType type, OmniTone expected)
    {
        var result = InvokePrivateStatic(typeof(ModuleLinksTab), "GetTypeBadge", type);
        Assert.Equal(expected, result);
    }

    // --- ServerPortsentrySection.GetBlockedCountBadgeStyle ---
    // Source: 0 => Success, <= 5 => Warning, _ => Danger

    [Theory]
    [InlineData(0, OmniTone.Success)]
    [InlineData(3, OmniTone.Warning)]
    [InlineData(5, OmniTone.Warning)]
    [InlineData(6, OmniTone.Danger)]
    [InlineData(50, OmniTone.Danger)]
    public void ServerPortsentrySection_GetBlockedCountBadgeStyle(int count, OmniTone expected)
    {
        var result = InvokePrivateStatic(typeof(ServerPortsentrySection), "GetBlockedCountBadgeStyle", count);
        Assert.Equal(expected, result);
    }

    // --- UserEdit.GetPermissionBadgeStyle ---
    // Source: Admin => Danger, Write => Warning, _ => Info

    [Theory]
    [InlineData(Permission.Read, OmniTone.Accent)]
    [InlineData(Permission.Write, OmniTone.Warning)]
    [InlineData(Permission.Admin, OmniTone.Danger)]
    public void UserEdit_GetPermissionBadgeStyle(Permission perm, OmniTone expected)
    {
        var result = InvokePrivateStatic(typeof(Aetheus.Front.Components.Users.UserEdit), "GetPermissionBadgeStyle", perm);
        Assert.Equal(expected, result);
    }

    // --- ProjectServersSection.GetTypeBadge ---
    // Source: AgentServer => Info, ExternalHost => Warning, _ => Light

    [Theory]
    [InlineData(ProjectServerType.AgentServer, OmniTone.Accent)]
    [InlineData(ProjectServerType.ExternalHost, OmniTone.Warning)]
    public void ProjectServersSection_GetTypeBadge(ProjectServerType type, OmniTone expected)
    {
        var result = InvokePrivateStatic(typeof(Aetheus.Front.Components.Projects.ProjectDetailSections.ProjectServersSection), "GetTypeBadge", type);
        Assert.Equal(expected, result);
    }
}
