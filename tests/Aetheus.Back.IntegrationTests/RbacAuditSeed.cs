// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// RBAC audit (ADR-059): one organization holding one of every organization-owned resource the
/// audit's write endpoints reference (project, server, environment carrying the server, project
/// server, vault with a secret, library with an entry, pipeline), and users holding built-in roles
/// for one or several organizations. Shared by <see cref="RbacAuditIntegrationTests"/> and
/// <see cref="CrossOrganizationWriteMatrixIntegrationTests"/>.
/// </summary>
internal static class RbacAuditSeed
{
    /// <summary>The key the vault and the library of every tenant carry.</summary>
    public const string SharedKey = "SHARED_KEY";

    public const string PipelineYaml =
        "name: rbac\nstages:\n  - name: Build\n    steps:\n      - name: Say\n        shell: echo one";

    internal sealed record Tenant(
        int OrganizationId, int ProjectId, int ServerId, int EnvironmentId, int ProjectServerId,
        int VaultId, int LibraryId, int PipelineId, string Marker)
    {
        /// <summary>The name of the tenant's vault and of its library (unique per tenant).</summary>
        public string SharedName => $"{Marker}-shared";
    }

    public static async Task<Tenant> SeedTenantAsync(IServiceProvider services, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var marker = $"rbac-{label}-{Guid.NewGuid():N}";

        var organization = new Organization { Name = marker, Slug = $"r{label}{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = $"{marker}-project", OrganizationId = organization.Id };
        var server = new Server
        {
            Name = $"{marker}-server",
            Hostname = $"{marker}.test",
            OrganizationId = organization.Id,
            Status = ServerStatus.Online,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);

        var environment = new Environment { Name = $"{marker}-env", ProjectId = project.Id };
        environment.Servers.Add(new EnvironmentServer { ServerId = server.Id });
        var projectServer = new ProjectServer
        {
            ProjectId = project.Id,
            ServerId = server.Id,
            Type = ProjectServerType.AgentServer,
            DisplayName = $"{marker}-ps",
            Host = $"{marker}.test"
        };
        var vault = new Vault { Name = $"{marker}-shared", Description = marker, ProjectId = project.Id };
        vault.Secrets.Add(new VaultSecret { Key = SharedKey, EncryptedValue = encryption.EncryptValue($"{label}-secret") });
        var library = new VariableLibrary { Name = $"{marker}-shared", Description = marker, ProjectId = project.Id };
        library.Entries.Add(new VariableLibraryEntry { Key = SharedKey, Value = $"{label}-value" });
        var pipeline = new Pipeline
        {
            Name = $"{marker}-pipeline",
            ProjectId = project.Id,
            YamlDefinition = PipelineYaml,
            CreatedByUsername = "admin"
        };
        db.AddRange(environment, projectServer, vault, library, pipeline);
        db.ServerPortRanges.Add(new ServerPortRange { ServerId = server.Id, From = 42000, To = 42050, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);

        return new Tenant(organization.Id, project.Id, server.Id, environment.Id, projectServer.Id,
            vault.Id, library.Id, pipeline.Id, marker);
    }

    /// <summary>A user holding each built-in <c>(role, organization)</c> pair, as a member of every
    /// organization named (membership level mapped from the role, R-47).</summary>
    public static async Task<(int Id, string Username)> CreateUserAsync(
        IServiceProvider services, string prefix, params (string Role, int OrganizationId)[] holdings)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Username = $"{prefix}-{Guid.NewGuid():N}",
            Email = $"{prefix}-{Guid.NewGuid():N}@rbac.test",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(IntegrationUsers.Password),
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await GrantAsync(db, user.Id, holdings, ct);
        return (user.Id, user.Username);
    }

    /// <summary>Adds built-in role holdings (and the memberships they imply) to an existing user.</summary>
    public static async Task GrantAsync(IServiceProvider services, int userId, params (string Role, int OrganizationId)[] holdings)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await GrantAsync(db, userId, holdings, ct);
    }

    private static async Task GrantAsync(
        AppDbContext db, int userId, (string Role, int OrganizationId)[] holdings, CancellationToken ct)
    {
        foreach (var (roleName, organizationId) in holdings)
        {
            var roleId = await db.Roles.Where(role => role.Name == roleName).Select(role => role.Id).SingleAsync(ct);
            db.UserOrganizationRoles.Add(new UserOrganizationRole { UserId = userId, OrganizationId = organizationId, RoleId = roleId });
            var level = RoleNames.MembershipFor(roleName);
            var member = await db.OrganizationMembers
                .SingleOrDefaultAsync(item => item.UserId == userId && item.OrganizationId == organizationId, ct);
            if (member is null)
                db.OrganizationMembers.Add(new OrganizationMember { UserId = userId, OrganizationId = organizationId, Role = level });
            else if (member.Role < level)
                member.Role = level;
        }
        await db.SaveChangesAsync(ct);
    }
}
