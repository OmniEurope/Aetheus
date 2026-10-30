// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// R-11 (owner decision): every sensitive action is its own named permission, and roles are assembled
/// from those names. This guard keeps the three ends of that contract tied together:
/// <list type="bullet">
/// <item>each converted endpoint checks its key (a revert to the level check would silently hand the
/// action back to every role holding the level);</item>
/// <item>each action key of the catalogue is checked by at least one endpoint (a key nobody checks is a
/// box in the role editor that grants nothing);</item>
/// <item>each level check still written as <c>ResourceType.X ... Permission.Y</c> maps to a catalogue level
/// key, so a role's ticked levels are exactly what those checks read;</item>
/// <item>the migration backfill gives each action key to the roles holding its legacy level, as the
/// catalogue says.</item>
/// </list>
/// </summary>
public sealed partial class PermissionKeyEnforcementAuditTests
{
    private const string Components = "src/Aetheus.Back/Components/";

    /// <summary>Converted endpoint ({controller file}:{method}) and the key it must check, directly or
    /// through the named private helper.</summary>
    private static readonly (string Site, string Key, string? Helper)[] Converted =
    [
        ("Servers/ServersController.cs:ExecuteServiceAction", nameof(PermissionKeys.ServerServicesControl), null),
        ("Servers/ServersController.cs:InstallService", nameof(PermissionKeys.ServerServicesControl), null),
        ("Servers/ServersController.cs:UninstallService", nameof(PermissionKeys.ServerServicesControl), null),
        ("Servers/ServersController.cs:UpgradeSystem", nameof(PermissionKeys.ServerSystemUpgrade), null),
        ("Servers/ServersController.cs:FirewallAllow", nameof(PermissionKeys.ServerFirewallManage), null),
        ("Servers/ServersController.cs:FirewallDeny", nameof(PermissionKeys.ServerFirewallManage), null),
        ("Servers/ServersController.cs:FirewallDeleteRule", nameof(PermissionKeys.ServerFirewallManage), null),
        ("Servers/ServersController.cs:FirewallToggle", nameof(PermissionKeys.ServerFirewallManage), null),
        ("PortRegistry/PortRegistryController.cs:CreateReservation", nameof(PermissionKeys.ServerPortsReserve), null),
        ("PortRegistry/PortRegistryController.cs:SetRange", nameof(PermissionKeys.ServerPortsReserve), null),
        ("PortRegistry/PortRegistryController.cs:Allocate", nameof(PermissionKeys.ServerPortsReserve), null),
        ("PortRegistry/PortRegistryController.cs:ReleaseReservation", nameof(PermissionKeys.ServerPortsReserve), null),
        ("PortAllocation/PortAllocationController.cs:Allocate", nameof(PermissionKeys.ServerPortsReserve), null),
        ("PortAllocation/PortAllocationController.cs:Release", nameof(PermissionKeys.ServerPortsReserve), null),
        ("Docker/DockerController.cs:GetContainerEnvVars", nameof(PermissionKeys.ServerDockerContainerEnvView), null),
        ("Vaults/VaultsController.cs:RevealSecretValue", nameof(PermissionKeys.VaultSecretReveal), null),
        ("Releases/ReleasesController.cs:Rollback", nameof(PermissionKeys.ReleaseRollback), null),
        ("Releases/ReleasesController.cs:RestoreStatus", nameof(PermissionKeys.ReleaseStatusRestore), null),
        ("Releases/ReleasesController.cs:TriggerBuild", nameof(PermissionKeys.PipelineRun), "AuthorizePipelineLaunchAsync"),
        ("Releases/ReleasesController.cs:RelaunchFromCommit", nameof(PermissionKeys.PipelineRun), "AuthorizePipelineLaunchAsync"),
        ("Pipelines/PipelinesController.cs:TriggerRun", nameof(PermissionKeys.PipelineRun), null),
        ("Pipelines/PipelinesController.cs:GetRunParameters", nameof(PermissionKeys.PipelineRun), null),
        ("Pipelines/PipelinesController.cs:Preflight", nameof(PermissionKeys.PipelineRun), null),
        ("Pipelines/PipelinesController.cs:CancelRun", nameof(PermissionKeys.PipelineRun), null),
        ("Pipelines/PipelinesController.cs:ResumeRun", nameof(PermissionKeys.PipelineRun), null),
        ("Pipelines/PipelinesController.cs:RetryFailedSteps", nameof(PermissionKeys.PipelineRun), null),
        ("Pipelines/PipelinesController.cs:Rerun", nameof(PermissionKeys.PipelineRun), null),
        ("Pipelines/PipelinesController.cs:DecideApproval", nameof(PermissionKeys.PipelineApprovalDecide), null),
        ("Mail/Mailbox/MailboxController.cs:GetFolders", nameof(PermissionKeys.ServerMailboxRead), null),
        ("Mail/Mailbox/MailboxController.cs:GetMessages", nameof(PermissionKeys.ServerMailboxRead), null),
        ("Mail/Mailbox/MailboxController.cs:GetMessage", nameof(PermissionKeys.ServerMailboxRead), null),
        ("Mail/Mailbox/MailboxController.cs:GetAttachment", nameof(PermissionKeys.ServerMailboxRead), null),
        ("Mail/Mailbox/MailboxController.cs:CreateFolder", nameof(PermissionKeys.ServerMailboxManage), null),
        ("Mail/Mailbox/MailboxController.cs:SetRead", nameof(PermissionKeys.ServerMailboxManage), null),
        ("Mail/Mailbox/MailboxController.cs:Delete", nameof(PermissionKeys.ServerMailboxManage), null),
        ("Mail/Mailbox/MailboxController.cs:Send", nameof(PermissionKeys.ServerMailboxManage), null),
        ("Organizations/OrganizationsController.cs:AddMember", nameof(PermissionKeys.OrganizationMembersManage), null),
        ("Organizations/OrganizationsController.cs:UpdateMember", nameof(PermissionKeys.OrganizationMembersManage), null),
        ("Organizations/OrganizationsController.cs:RemoveMember", nameof(PermissionKeys.OrganizationMembersManage), null),
        // Administration section for organization admins (owner decision 2026-10-28): each page is scoped by its key.
        ("Organizations/OrganizationsController.cs:GetOrganizations", nameof(PermissionKeys.OrganizationMembersManage), null),
        ("Users/UsersController.cs:GetUsers", nameof(PermissionKeys.OrganizationMembersManage), null),
        ("Users/UsersController.cs:CreateUser", nameof(PermissionKeys.OrganizationMembersManage), null),
        ("Auth/RolesController.cs:GetRoles", nameof(PermissionKeys.OrganizationRolesManage), null),
        ("Auth/RolesController.cs:GetAssignableRoles", nameof(PermissionKeys.OrganizationMembersManage), null),
        ("Auth/RolesController.cs:CreateRole",nameof(PermissionKeys.OrganizationRolesManage), null),
        ("Auth/RolesController.cs:GetRole", nameof(PermissionKeys.OrganizationRolesManage), "ResolveRoleAccessAsync"),
        ("Auth/RolesController.cs:UpdateRole", nameof(PermissionKeys.OrganizationRolesManage), "ResolveRoleAccessAsync"),
        ("Auth/RolesController.cs:DeleteRole", nameof(PermissionKeys.OrganizationRolesManage), "ResolveRoleAccessAsync"),
        ("Auth/RolesController.cs:GetPermissions", nameof(PermissionKeys.OrganizationRolesManage), "ResolveRoleAccessAsync"),
        ("Auth/RolesController.cs:SetPermissions", nameof(PermissionKeys.OrganizationRolesManage), "ResolveRoleAccessAsync"),
        ("Auth/RolesController.cs:GetPermissionKeys", nameof(PermissionKeys.OrganizationRolesManage), "ResolveRoleAccessAsync"),
        ("Auth/RolesController.cs:SetPermissionKeys", nameof(PermissionKeys.OrganizationRolesManage), "ResolveRoleAccessAsync"),
        ("Auth/RolesController.cs:CloneRole", nameof(PermissionKeys.OrganizationRolesManage), "ResolveRoleAccessAsync"),
        ("Auth/RolesController.cs:GetUsersInRole", nameof(PermissionKeys.OrganizationRolesManage), "ResolveRoleAccessAsync"),
        ("Auth/RolesController.cs:GetUsersAvailableForRole", nameof(PermissionKeys.OrganizationRolesManage), "ResolveRoleAccessAsync"),
        ("Auth/RolesController.cs:AddUserToRole", nameof(PermissionKeys.OrganizationRolesManage), "ResolveRoleAccessAsync"),
        ("Auth/RolesController.cs:RemoveUserFromRole", nameof(PermissionKeys.OrganizationRolesManage), "ResolveRoleAccessAsync"),
        ("Notifications/MailTemplates/MailTemplatesController.cs:List", nameof(PermissionKeys.OrganizationMailTemplatesManage), "MayEditAsync"),
        ("Notifications/MailTemplates/MailTemplatesController.cs:Get", nameof(PermissionKeys.OrganizationMailTemplatesManage), "MayEditAsync"),
        ("Notifications/MailTemplates/MailTemplatesController.cs:Save", nameof(PermissionKeys.OrganizationMailTemplatesManage), "MayEditAsync"),
        ("Notifications/MailTemplates/MailTemplatesController.cs:Reset", nameof(PermissionKeys.OrganizationMailTemplatesManage), "MayEditAsync"),
        ("Notifications/MailTemplates/MailTemplatesController.cs:SendTest", nameof(PermissionKeys.OrganizationMailTemplatesManage), "MayEditAsync"),
    ];

    [GeneratedRegex(@"\bPermissionKeys\.(\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex KeyReference();

    [GeneratedRegex(@"HasPermissionAsync\(\s*\w+,\s*ResourceType\.(\w+),[^;]*?Permission\.(Read|Write|Admin)", RegexOptions.CultureInvariant)]
    private static partial Regex LevelCheck();

    [GeneratedRegex(@"\('([a-z.]+)',\s*(\d+),\s*(\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex BackfillTuple();

    private static string ComponentsDirectory => Path.Combine(RepositoryScan.Root, "src", "Aetheus.Back", "Components");

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepositoryScan.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The text of one method, from its declaration to the next member of the class.</summary>
    private static string MethodBody(string source, string method, string site)
    {
        var start = Regex.Match(source, $@"\n\s+(public|private)\s+(static\s+)?async\s+Task[^\n]*?\s{method}\(", RegexOptions.None, TimeSpan.FromSeconds(2));
        Assert.True(start.Success, $"{site}: method not found; update the R-11 map if it was renamed.");
        var next = Regex.Match(source[(start.Index + 1)..], @"\n    (\[|public |private |internal |protected )", RegexOptions.None, TimeSpan.FromSeconds(2));
        return next.Success ? source.Substring(start.Index, next.Index + 1) : source[start.Index..];
    }

    [Fact]
    public void EveryConvertedAction_ChecksItsNamedPermission()
    {
        var missing = new List<string>();
        foreach (var (site, key, helper) in Converted)
        {
            var (file, method) = (site[..site.IndexOf(':')], site[(site.IndexOf(':') + 1)..]);
            var source = Read(Components + file);
            var body = MethodBody(source, method, site);
            var checks = helper is null
                ? body.Contains($"PermissionKeys.{key}", StringComparison.Ordinal)
                : body.Contains(helper, StringComparison.Ordinal)
                  && MethodBody(source, helper, site).Contains($"PermissionKeys.{key}", StringComparison.Ordinal);
            if (!checks)
                missing.Add($"{site} must check PermissionKeys.{key}");
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EveryActionKeyOfTheCatalogue_IsCheckedByAnEndpoint()
    {
        var referenced = RepositoryScan.Enumerate(ComponentsDirectory, "*Controller.cs")
            .SelectMany(file => KeyReference().Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        var constants = typeof(PermissionKeys).GetFields()
            .Where(field => field.IsLiteral)
            .ToDictionary(field => (string)field.GetRawConstantValue()!, field => field.Name, StringComparer.Ordinal);

        var unchecked_ = PermissionCatalog.All
            .Where(definition => !definition.IsLevel)
            .Where(definition => !referenced.Contains(constants[definition.Key]))
            .Select(definition => definition.Key)
            .ToList();

        Assert.True(unchecked_.Count == 0,
            "Catalogue keys no endpoint checks (a role box granting nothing): " + string.Join(", ", unchecked_));
    }

    [Fact]
    public void EveryKeyConstant_IsInTheCatalogue()
    {
        var keys = typeof(PermissionKeys).GetFields().Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!).ToList();

        Assert.All(keys, key => Assert.NotNull(PermissionCatalog.Find(key)));
        Assert.Equal(keys.Count, PermissionCatalog.All.Count);
    }

    [Fact]
    public void EveryLevelCheck_MapsToACatalogueLevelKey()
    {
        var unmapped = new SortedSet<string>(StringComparer.Ordinal);
        var seen = 0;
        foreach (var file in RepositoryScan.Enumerate(ComponentsDirectory, "*.cs"))
        {
            foreach (Match match in LevelCheck().Matches(File.ReadAllText(file)))
            {
                seen++;
                var type = Enum.Parse<ResourceType>(match.Groups[1].Value);
                var level = Enum.Parse<Permission>(match.Groups[2].Value);
                // Agent pools and the Docker platform belong to no organization: site Admin only, no key.
                if (type is ResourceType.AgentPool or ResourceType.Docker)
                    continue;
                if (PermissionCatalog.LevelKey(type, level) is null)
                    unmapped.Add($"{type}.{level}");
            }
        }

        Assert.True(seen > 100, $"Only {seen} level checks found: the scan pattern no longer matches the code.");
        Assert.True(unmapped.Count == 0, "Level checks without a catalogue key: " + string.Join(", ", unmapped));
    }

    [Fact]
    public void TheBackfill_GivesEachActionKeyToTheRolesHoldingItsLegacyLevel()
    {
        var migration = Read("src/Aetheus.Back/Data/Migrations/20261028180000_AddRolePermissionGrants.cs");
        var backfilled = BackfillTuple().Matches(migration)
            .ToDictionary(match => match.Groups[1].Value,
                match => ((ResourceType)int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                          (Permission)int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)),
                StringComparer.Ordinal);

        var expected = PermissionCatalog.All
            .Where(definition => definition is { IsLevel: false, ResourceType: not null })
            .ToDictionary(definition => definition.Key,
                definition => (definition.ResourceType!.Value, definition.LegacyLevel!.Value), StringComparer.Ordinal);

        Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            backfilled.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        // The owner's changes, built-in roles only: the container environment leaves Reader and Contributor,
        // the firewall and the system upgrade leave Contributor.
        Assert.Contains("k.permission_key IN ('server.firewall.manage', 'server.system.upgrade')", migration, StringComparison.Ordinal);
        Assert.Contains("'server.docker.container.env.view'", migration, StringComparison.Ordinal);
        Assert.Contains("r.\"Name\" IN ('Reader', 'Contributor')", migration, StringComparison.Ordinal);
    }
}
