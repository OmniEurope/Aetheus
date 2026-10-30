// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Auth;

/// <summary>
/// The named permissions a role is assembled from (R-11). A level key (<c>*.view</c>, <c>*.edit</c>,
/// <c>*.manage</c>) is the Read, Write or Admin level of one resource type and is honoured by every
/// level check of that type; any other key gates one sensitive action of its own. The definitions
/// (resource type, default roles) live in <see cref="PermissionCatalog"/>.
/// </summary>
public static class PermissionKeys
{
    // Levels, one triple per organization-owned resource type.
    public const string ProjectView = "project.view";
    public const string ProjectEdit = "project.edit";
    public const string ProjectManage = "project.manage";
    public const string PipelineView = "pipeline.view";
    public const string PipelineEdit = "pipeline.edit";
    public const string PipelineManage = "pipeline.manage";
    public const string PipelineTemplateView = "pipeline.template.view";
    public const string PipelineTemplateEdit = "pipeline.template.edit";
    public const string PipelineTemplateManage = "pipeline.template.manage";
    public const string ReleaseView = "release.view";
    public const string ReleaseEdit = "release.edit";
    public const string ReleaseManage = "release.manage";
    public const string EnvironmentView = "environment.view";
    public const string EnvironmentEdit = "environment.edit";
    public const string EnvironmentManage = "environment.manage";
    public const string VariablesView = "variables.view";
    public const string VariablesEdit = "variables.edit";
    public const string VariablesManage = "variables.manage";
    public const string VaultView = "vault.view";
    public const string VaultEdit = "vault.edit";
    public const string VaultManage = "vault.manage";
    public const string ServerView = "server.view";
    public const string ServerEdit = "server.edit";
    public const string ServerManage = "server.manage";
    public const string ConnectionView = "connection.view";
    public const string ConnectionEdit = "connection.edit";
    public const string ConnectionManage = "connection.manage";

    // Actions with a permission of their own.
    /// <summary>Start, rerun, resume, retry or cancel a run (and read its launch parameters).</summary>
    public const string PipelineRun = "pipeline.run";
    /// <summary>Approve or reject a pending approval; the approver also administers the gated scope.</summary>
    public const string PipelineApprovalDecide = "pipeline.approval.decide";
    /// <summary>Read a vault secret's value in clear.</summary>
    public const string VaultSecretReveal = "vault.secret.reveal";
    /// <summary>Allow, deny, delete a firewall rule, or switch the firewall on or off.</summary>
    public const string ServerFirewallManage = "server.firewall.manage";
    /// <summary>Upgrade the server's system packages.</summary>
    public const string ServerSystemUpgrade = "server.system.upgrade";
    /// <summary>Start, stop or restart a service, install or uninstall one.</summary>
    public const string ServerServicesControl = "server.services.control";
    /// <summary>Reserve, allocate or release ports, or change the server's port range.</summary>
    public const string ServerPortsReserve = "server.ports.reserve";
    /// <summary>Read a Docker container's environment variables (they often carry secrets).</summary>
    public const string ServerDockerContainerEnvView = "server.docker.container.env.view";
    /// <summary>Open a mailbox of the server's mail stack and read its messages.</summary>
    public const string ServerMailboxRead = "server.mailbox.read";
    /// <summary>Send, delete, file or mark messages in a mailbox of the server's mail stack.</summary>
    public const string ServerMailboxManage = "server.mailbox.manage";
    /// <summary>Roll a release back; the caller also writes the release's project.</summary>
    public const string ReleaseRollback = "release.rollback";
    /// <summary>Put a release's recorded status back; the caller also administers its project.</summary>
    public const string ReleaseStatusRestore = "release.status.restore";
    /// <summary>Read an organization's members and add, change or remove them.</summary>
    public const string OrganizationMembersManage = "org.members.manage";
    /// <summary>Create, change and delete the roles of an organization, and give roles to its members.</summary>
    public const string OrganizationRolesManage = "org.roles.manage";
    /// <summary>Change the organization's own version of the mail templates its notifications are sent with.</summary>
    public const string OrganizationMailTemplatesManage = "org.mailtemplates.manage";
}
