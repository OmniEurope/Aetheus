// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.Data;

public static class DbInitializer
{
    public static async Task SeedAsync(AppDbContext db, IConfiguration? config = null)
    {
        if (await db.AppSettings.AnyAsync())
            return;

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync()
            : null;

        db.AppSettings.AddRange(
            new AppSetting { Key = "SiteName", Value = "Aetheus" },
            new AppSetting { Key = "MetricsRetentionDays", Value = "30" }
        );

        await db.SaveChangesAsync();

        await SeedAdminUserAsync(db, config);
        await SeedPipelineTemplatesAsync(db);

        if (transaction != null)
            await transaction.CommitAsync();
    }

    /// <summary>
    /// Default organization that owns the seeded admin and any newly created resource that
    /// is not explicitly assigned to another organization. The whole RBAC model assumes
    /// every Server / Project / Plugin is owned by an organization, so this row must always
    /// exist after seed.
    /// </summary>
    private const string DefaultOrganizationName = "Aetheus";
    private const string DefaultOrganizationSlug = "aetheus";

    private static async Task<Organization> EnsureDefaultOrganizationAsync(AppDbContext db)
    {
        var existing = await db.Organizations.FirstOrDefaultAsync(o => o.Slug == DefaultOrganizationSlug);
        if (existing is not null) return existing;

        var org = new Organization
        {
            Name = DefaultOrganizationName,
            Slug = DefaultOrganizationSlug,
            Description = "Default organization seeded on first run; owns all bootstrap resources."
        };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    private static async Task SeedAdminUserAsync(AppDbContext db, IConfiguration? config)
    {
        if (await db.Users.AnyAsync())
            return;

        var adminPassword = config?["Auth:AdminPassword"];

        // F-06: Refuse to seed a default/well-known admin password. The non-relational
        // (in-memory) test path is the only allowed bypass - tests skip it via their own
        // configuration and are exempted by the IsRelational() check.
        if (db.Database.IsRelational() &&
            (string.IsNullOrWhiteSpace(adminPassword) ||
             string.Equals(adminPassword, "admin", StringComparison.OrdinalIgnoreCase) ||
             adminPassword.Length < 12))
        {
            throw new InvalidOperationException(
                "Auth:AdminPassword is missing or too weak. Set a strong (>= 12 chars, not 'admin') password before first run.");
        }

        if (string.IsNullOrWhiteSpace(adminPassword))
            adminPassword = "admin";

        var adminRole = new Role { Name = "Admin", Description = "Full access" };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();

        var adminUser = new User
        {
            Username = SeededAdminEmail.SeededUsername,
            Email = SeededAdminEmail.Resolve(config),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword),
            IsActive = true
        };
        db.Users.Add(adminUser);
        await db.SaveChangesAsync();

        db.UserRoles.Add(new UserRole { UserId = adminUser.Id, RoleId = adminRole.Id });
        await db.SaveChangesAsync();

        // Every user must belong to at least one organization to interact with the platform.
        // The seeded admin owns the default "Aetheus" org so the bootstrap state is usable.
        var defaultOrg = await EnsureDefaultOrganizationAsync(db);
        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = defaultOrg.Id,
            UserId = adminUser.Id,
            Role = OrganizationRole.Owner
        });
        await db.SaveChangesAsync();

        // F-23: seed Admin-level ResourcePermission for every ResourceType so the user-edit
        // page reports the Admin role's effective permissions instead of "Aucune permission assignée".
        foreach (var rt in Enum.GetValues<ResourceType>())
        {
            db.ResourcePermissions.Add(new ResourcePermission
            {
                RoleId = adminRole.Id,
                ResourceType = rt,
                ResourceId = null,
                Permission = Permission.Admin
            });
        }
        db.RolePermissionGrants.AddRange(BuiltInRoleGrants.For(adminRole.Id, adminRole.Name));
        await db.SaveChangesAsync();

        await SeedDefaultRolesAsync(db);

        // R-45: the seeded admin is the default organization's Owner, so it also holds the role that
        // membership level gives (ADR-059), like any member added later.
        var organizationAdmin = await db.Roles.SingleAsync(r => r.OrganizationId == null && r.Name == RoleNames.OrganizationAdmin);
        db.UserOrganizationRoles.Add(new UserOrganizationRole
        {
            UserId = adminUser.Id,
            OrganizationId = defaultOrg.Id,
            RoleId = organizationAdmin.Id
        });
        await db.SaveChangesAsync();
    }

    // The roles held for one organization (ADR-059), each with the same permission on every resource type.
    private static readonly (string Name, string Description, Permission Permission)[] OrganizationRoles =
    [
        (RoleNames.Reader, "Read access to the resources of its organization", Permission.Read),
        (RoleNames.Contributor, "Read and write access to the resources of its organization", Permission.Write),
        (RoleNames.OrganizationAdmin, "Full access to the resources of its organization, and management of its members", Permission.Admin)
    ];

    private static async Task SeedDefaultRolesAsync(AppDbContext db)
    {
        foreach (var (name, description, permission) in OrganizationRoles)
        {
            if (await db.Roles.AnyAsync(r => r.OrganizationId == null && r.Name == name)) continue;

            var role = new Role { Name = name, Description = description };
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            foreach (var rt in Enum.GetValues<ResourceType>())
                db.ResourcePermissions.Add(new ResourcePermission
                {
                    RoleId = role.Id,
                    ResourceType = rt,
                    ResourceId = null,
                    Permission = permission
                });
            db.RolePermissionGrants.AddRange(BuiltInRoleGrants.For(role.Id, name));
            await db.SaveChangesAsync();
        }
    }

    private static async Task SeedPipelineTemplatesAsync(AppDbContext db)
    {
        if (await db.PipelineTemplates.AnyAsync())
            return;

        var defaultOrganization = await EnsureDefaultOrganizationAsync(db);

        db.PipelineTemplates.AddRange(
            new PipelineTemplate
            {
                Name = "Build=Windows, Deploy=VPS",
                Description = "Build on Windows, deploy to Linux VPS via SCP",
                Category = "CI/CD",
                YamlContent = """
                    name: ci-win-to-vps
                    trigger: manual
                    variable_libraries:
                      - deploy-config
                    vaults:
                      - ci-secrets
                    stages:
                      - name: Compile
                        group: Build
                        steps:
                          - name: Restore & Build
                            shell: dotnet build $(WORKSPACE)/Aetheus.slnx --configuration Release --nologo -v q
                            working_directory: $(WORKSPACE)
                            timeout_seconds: 300
                      - name: Tests
                        group: Build
                        depends_on: [Compile]
                        steps:
                          - name: Run Back Tests
                            shell: dotnet test --project $(WORKSPACE)/tests/Aetheus.Back.Tests --no-build -c Release --output Detailed
                            timeout_seconds: 300
                          - name: Run Front Tests
                            shell: dotnet test --project $(WORKSPACE)/tests/Aetheus.Front.Tests --no-build -c Release --output Detailed
                            timeout_seconds: 300
                          - name: Run Agent Tests
                            shell: dotnet test --project $(WORKSPACE)/tests/Aetheus.Agent.Core.Tests --no-build -c Release --output Detailed
                            timeout_seconds: 300
                      - name: Coverage
                        group: Build
                        depends_on: [Tests]
                        steps:
                          - name: Collect Coverage
                            shell: |
                              dotnet test --project $(WORKSPACE)/tests/Aetheus.Back.Tests --no-build -c Release --coverlet --results-directory $(WORKSPACE)/coverage
                              if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                              dotnet test --project $(WORKSPACE)/tests/Aetheus.Front.Tests --no-build -c Release --coverlet --results-directory $(WORKSPACE)/coverage
                              if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                              dotnet test --project $(WORKSPACE)/tests/Aetheus.Agent.Core.Tests --no-build -c Release --coverlet --results-directory $(WORKSPACE)/coverage
                              if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                              # coverlet.MTP stamps its report; give it the coverage.cobertura.xml layout the readers expect.
                              Get-ChildItem -Path $(WORKSPACE)/coverage -Filter 'coverage.cobertura.*.xml' | ForEach-Object { $dir = Join-Path $_.DirectoryName ($_.BaseName -replace '^coverage\.cobertura\.', ''); New-Item -ItemType Directory -Force -Path $dir | Out-Null; Move-Item $_.FullName (Join-Path $dir 'coverage.cobertura.xml') }
                              $reports = @(Get-ChildItem -Path $(WORKSPACE)/coverage -Recurse -Filter coverage.cobertura.xml)
                              if ($reports.Count -ne 3) { Write-Error "Expected exactly 3 coverage reports, found $($reports.Count)."; exit 1 }
                            timeout_seconds: 300
                      # The coverage report reads the files Collect Coverage writes: steps of one stage run concurrently (ADR-012).
                      - name: CoverageReport
                        group: Build
                        depends_on: [Coverage]
                        steps:
                          - name: Publish Coverage
                            type: coverage
                      - name: Lint
                        group: Build
                        depends_on: [Compile]
                        steps:
                          - name: Check Formatting
                            shell: dotnet format $(WORKSPACE)/Aetheus.slnx --verify-no-changes --no-restore -v diag
                            timeout_seconds: 120
                            continue_on_error: true
                      - name: Publish
                        group: Build
                        depends_on: [CoverageReport, Lint]
                        artifacts:
                          - $(WORKSPACE)/publish/back
                        steps:
                          - name: Publish Back
                            shell: dotnet publish $(WORKSPACE)/src/Aetheus.Back -c Release -o $(WORKSPACE)/publish/back --nologo -v q
                            timeout_seconds: 120
                      - name: Release
                        group: Build
                        depends_on: [Publish]
                        steps:
                          - name: Create Release
                            type: release
                            version: "1.0.$(BUILD_BUILDID)"
                            changelog: true
                      - name: Substitute
                        group: Deploy
                        depends_on: [Release]
                        steps:
                          - name: Variable Substitution
                            type: substitute
                            target_files:
                              - $(WORKSPACE)/publish/back/appsettings.json
                      - name: Upload
                        group: Deploy
                        depends_on: [Substitute]
                        steps:
                          # SECURITY: StrictHostKeyChecking=no + UserKnownHostsFile=/dev/null disable SSH
                          # host-key verification (MITM exposure). Kept for a copy-paste demo template that
                          # has no pre-seeded known_hosts. Before production use, harden to
                          # StrictHostKeyChecking=accept-new (trust-on-first-use) or pre-populate the
                          # target host key in a known_hosts file.
                          - name: Upload via SCP
                            shell: sshpass -e scp -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -P $(TARGET_SSH_PORT) -r $(WORKSPACE)/publish/back/* $(TARGET_USER)@$(TARGET_HOST):$(TARGET_DIR)/
                            timeout_seconds: 180
                      # Steps of one stage run concurrently (ADR-012): each deploy step waits for the previous stage (DEP-008).
                      - name: Restart
                        group: Deploy
                        depends_on: [Upload]
                        steps:
                          - name: Restart Service
                            shell: sshpass -e ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -p $(TARGET_SSH_PORT) $(TARGET_USER)@$(TARGET_HOST) "sudo systemctl restart aetheus-back"
                            timeout_seconds: 60
                    """
            },
            new PipelineTemplate
            {
                Name = "Build=Windows, Deploy=Windows",
                Description = "Build and deploy on Windows (local copy + service restart)",
                Category = "CI/CD",
                YamlContent = """
                    name: ci-win-to-win
                    trigger: manual
                    variable_libraries:
                      - deploy-config
                    vaults:
                      - ci-secrets
                    stages:
                      - name: Compile
                        group: Build
                        steps:
                          - name: Restore & Build
                            shell: dotnet build $(WORKSPACE)/Aetheus.slnx --configuration Release --nologo -v q
                            working_directory: $(WORKSPACE)
                            timeout_seconds: 300
                      - name: Tests
                        group: Build
                        depends_on: [Compile]
                        steps:
                          - name: Run Back Tests
                            shell: dotnet test --project $(WORKSPACE)/tests/Aetheus.Back.Tests --no-build -c Release --output Detailed
                            timeout_seconds: 300
                          - name: Run Front Tests
                            shell: dotnet test --project $(WORKSPACE)/tests/Aetheus.Front.Tests --no-build -c Release --output Detailed
                            timeout_seconds: 300
                          - name: Run Agent Tests
                            shell: dotnet test --project $(WORKSPACE)/tests/Aetheus.Agent.Core.Tests --no-build -c Release --output Detailed
                            timeout_seconds: 300
                      - name: Coverage
                        group: Build
                        depends_on: [Tests]
                        steps:
                          - name: Collect Coverage
                            shell: |
                              dotnet test --project $(WORKSPACE)/tests/Aetheus.Back.Tests --no-build -c Release --coverlet --results-directory $(WORKSPACE)/coverage
                              if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                              dotnet test --project $(WORKSPACE)/tests/Aetheus.Front.Tests --no-build -c Release --coverlet --results-directory $(WORKSPACE)/coverage
                              if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                              dotnet test --project $(WORKSPACE)/tests/Aetheus.Agent.Core.Tests --no-build -c Release --coverlet --results-directory $(WORKSPACE)/coverage
                              if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                              # coverlet.MTP stamps its report; give it the coverage.cobertura.xml layout the readers expect.
                              Get-ChildItem -Path $(WORKSPACE)/coverage -Filter 'coverage.cobertura.*.xml' | ForEach-Object { $dir = Join-Path $_.DirectoryName ($_.BaseName -replace '^coverage\.cobertura\.', ''); New-Item -ItemType Directory -Force -Path $dir | Out-Null; Move-Item $_.FullName (Join-Path $dir 'coverage.cobertura.xml') }
                              $reports = @(Get-ChildItem -Path $(WORKSPACE)/coverage -Recurse -Filter coverage.cobertura.xml)
                              if ($reports.Count -ne 3) { Write-Error "Expected exactly 3 coverage reports, found $($reports.Count)."; exit 1 }
                            timeout_seconds: 300
                      # The coverage report reads the files Collect Coverage writes: steps of one stage run concurrently (ADR-012).
                      - name: CoverageReport
                        group: Build
                        depends_on: [Coverage]
                        steps:
                          - name: Publish Coverage
                            type: coverage
                      - name: Lint
                        group: Build
                        depends_on: [Compile]
                        steps:
                          - name: Check Formatting
                            shell: dotnet format $(WORKSPACE)/Aetheus.slnx --verify-no-changes --no-restore -v diag
                            timeout_seconds: 120
                            continue_on_error: true
                      - name: Publish
                        group: Build
                        depends_on: [CoverageReport, Lint]
                        artifacts:
                          - $(WORKSPACE)/publish/back
                        steps:
                          - name: Publish Back
                            shell: dotnet publish $(WORKSPACE)/src/Aetheus.Back -c Release -o $(WORKSPACE)/publish/back --nologo -v q
                            timeout_seconds: 120
                      - name: Release
                        group: Build
                        depends_on: [Publish]
                        steps:
                          - name: Create Release
                            type: release
                            version: "1.0.$(BUILD_BUILDID)"
                            changelog: true
                      - name: Substitute
                        group: Deploy
                        depends_on: [Release]
                        steps:
                          - name: Variable Substitution
                            type: substitute
                            target_files:
                              - $(WORKSPACE)/publish/back/appsettings.json
                      - name: Stop
                        group: Deploy
                        depends_on: [Substitute]
                        steps:
                          - name: Stop Service
                            shell: Stop-Service -Name "AetheusBack" -Force -ErrorAction SilentlyContinue
                            timeout_seconds: 30
                            continue_on_error: true
                      # Steps of one stage run concurrently (ADR-012): each deploy step waits for the previous stage (DEP-008).
                      - name: Copy
                        group: Deploy
                        depends_on: [Stop]
                        steps:
                          - name: Copy Files
                            shell: "robocopy $(WORKSPACE)/publish/back $(DEPLOY_DIR) /MIR /NFL /NDL /NJH /NJS /NP ; if ($LASTEXITCODE -le 7) { $LASTEXITCODE = 0 }"
                            timeout_seconds: 120
                      - name: Start
                        group: Deploy
                        depends_on: [Copy]
                        steps:
                          - name: Start Service
                            shell: Start-Service -Name "AetheusBack"
                            timeout_seconds: 30
                    """
            },
            new PipelineTemplate
            {
                Name = "Build=VPS, Deploy=VPS",
                Description = "Build and deploy on Linux VPS (local copy + systemd restart)",
                Category = "CI/CD",
                YamlContent = """
                    name: ci-vps-to-vps
                    trigger: manual
                    variable_libraries:
                      - deploy-config
                    vaults:
                      - ci-secrets
                    stages:
                      - name: Compile
                        group: Build
                        steps:
                          - name: Restore & Build
                            shell: dotnet build $(WORKSPACE)/Aetheus.slnx --configuration Release --nologo -v q
                            working_directory: $(WORKSPACE)
                            timeout_seconds: 300
                      - name: Tests
                        group: Build
                        depends_on: [Compile]
                        steps:
                          - name: Run Back Tests
                            shell: dotnet test --project $(WORKSPACE)/tests/Aetheus.Back.Tests --no-build -c Release --output Detailed
                            timeout_seconds: 300
                          - name: Run Front Tests
                            shell: dotnet test --project $(WORKSPACE)/tests/Aetheus.Front.Tests --no-build -c Release --output Detailed
                            timeout_seconds: 300
                          - name: Run Agent Tests
                            shell: dotnet test --project $(WORKSPACE)/tests/Aetheus.Agent.Core.Tests --no-build -c Release --output Detailed
                            timeout_seconds: 300
                      - name: Coverage
                        group: Build
                        depends_on: [Tests]
                        steps:
                          - name: Collect Coverage
                            shell: >-
                              dotnet test --project $(WORKSPACE)/tests/Aetheus.Back.Tests --no-build -c Release --coverlet --results-directory $(WORKSPACE)/coverage &&
                              dotnet test --project $(WORKSPACE)/tests/Aetheus.Front.Tests --no-build -c Release --coverlet --results-directory $(WORKSPACE)/coverage &&
                              dotnet test --project $(WORKSPACE)/tests/Aetheus.Agent.Core.Tests --no-build -c Release --coverlet --results-directory $(WORKSPACE)/coverage &&
                              sh $(WORKSPACE)/scripts/normalize-coverage-report.sh $(WORKSPACE)/coverage
                            timeout_seconds: 300
                      # The coverage report reads the files Collect Coverage writes: steps of one stage run concurrently (ADR-012).
                      - name: CoverageReport
                        group: Build
                        depends_on: [Coverage]
                        steps:
                          - name: Publish Coverage
                            type: coverage
                      - name: Lint
                        group: Build
                        depends_on: [Compile]
                        steps:
                          - name: Check Formatting
                            shell: dotnet format $(WORKSPACE)/Aetheus.slnx --verify-no-changes --no-restore -v diag
                            timeout_seconds: 120
                            continue_on_error: true
                      - name: Publish
                        group: Build
                        depends_on: [CoverageReport, Lint]
                        artifacts:
                          - $(WORKSPACE)/publish/back
                        steps:
                          - name: Publish Back
                            shell: dotnet publish $(WORKSPACE)/src/Aetheus.Back -c Release -o $(WORKSPACE)/publish/back --nologo -v q
                            timeout_seconds: 120
                      - name: Release
                        group: Build
                        depends_on: [Publish]
                        steps:
                          - name: Create Release
                            type: release
                            version: "1.0.$(BUILD_BUILDID)"
                            changelog: true
                      - name: Substitute
                        group: Deploy
                        depends_on: [Release]
                        steps:
                          - name: Variable Substitution
                            type: substitute
                            target_files:
                              - $(WORKSPACE)/publish/back/appsettings.json
                      - name: Stop
                        group: Deploy
                        depends_on: [Substitute]
                        steps:
                          - name: Stop Service
                            shell: sudo systemctl stop aetheus-back || true
                            timeout_seconds: 30
                      # Steps of one stage run concurrently (ADR-012): each deploy step waits for the previous stage (DEP-008).
                      - name: Copy
                        group: Deploy
                        depends_on: [Stop]
                        steps:
                          - name: Copy Files
                            shell: cp -r $(WORKSPACE)/publish/back/* $(TARGET_DIR)/
                            timeout_seconds: 120
                      - name: Start
                        group: Deploy
                        depends_on: [Copy]
                        steps:
                          - name: Start Service
                            shell: sudo systemctl start aetheus-back
                            timeout_seconds: 30
                    """
            },
            new PipelineTemplate
            {
                Name = "Build=VPS, Deploy=Windows",
                Description = "Build on Linux VPS, deploy to Windows via SCP",
                Category = "CI/CD",
                YamlContent = """
                    name: ci-vps-to-win
                    trigger: manual
                    variable_libraries:
                      - deploy-config
                    vaults:
                      - ci-secrets
                    stages:
                      - name: Compile
                        group: Build
                        steps:
                          - name: Restore & Build
                            shell: dotnet build $(WORKSPACE)/Aetheus.slnx --configuration Release --nologo -v q
                            working_directory: $(WORKSPACE)
                            timeout_seconds: 300
                      - name: Tests
                        group: Build
                        depends_on: [Compile]
                        steps:
                          - name: Run Back Tests
                            shell: dotnet test --project $(WORKSPACE)/tests/Aetheus.Back.Tests --no-build -c Release --output Detailed
                            timeout_seconds: 300
                          - name: Run Front Tests
                            shell: dotnet test --project $(WORKSPACE)/tests/Aetheus.Front.Tests --no-build -c Release --output Detailed
                            timeout_seconds: 300
                          - name: Run Agent Tests
                            shell: dotnet test --project $(WORKSPACE)/tests/Aetheus.Agent.Core.Tests --no-build -c Release --output Detailed
                            timeout_seconds: 300
                      - name: Coverage
                        group: Build
                        depends_on: [Tests]
                        steps:
                          - name: Collect Coverage
                            shell: >-
                              dotnet test --project $(WORKSPACE)/tests/Aetheus.Back.Tests --no-build -c Release --coverlet --results-directory $(WORKSPACE)/coverage &&
                              dotnet test --project $(WORKSPACE)/tests/Aetheus.Front.Tests --no-build -c Release --coverlet --results-directory $(WORKSPACE)/coverage &&
                              dotnet test --project $(WORKSPACE)/tests/Aetheus.Agent.Core.Tests --no-build -c Release --coverlet --results-directory $(WORKSPACE)/coverage &&
                              sh $(WORKSPACE)/scripts/normalize-coverage-report.sh $(WORKSPACE)/coverage
                            timeout_seconds: 300
                      # The coverage report reads the files Collect Coverage writes: steps of one stage run concurrently (ADR-012).
                      - name: CoverageReport
                        group: Build
                        depends_on: [Coverage]
                        steps:
                          - name: Publish Coverage
                            type: coverage
                      - name: Lint
                        group: Build
                        depends_on: [Compile]
                        steps:
                          - name: Check Formatting
                            shell: dotnet format $(WORKSPACE)/Aetheus.slnx --verify-no-changes --no-restore -v diag
                            timeout_seconds: 120
                            continue_on_error: true
                      - name: Publish
                        group: Build
                        depends_on: [CoverageReport, Lint]
                        artifacts:
                          - $(WORKSPACE)/publish/back
                        steps:
                          - name: Publish Back
                            shell: dotnet publish $(WORKSPACE)/src/Aetheus.Back -c Release -o $(WORKSPACE)/publish/back --nologo -v q
                            timeout_seconds: 120
                      - name: Release
                        group: Build
                        depends_on: [Publish]
                        steps:
                          - name: Create Release
                            type: release
                            version: "1.0.$(BUILD_BUILDID)"
                            changelog: true
                      - name: Substitute
                        group: Deploy
                        depends_on: [Release]
                        steps:
                          - name: Variable Substitution
                            type: substitute
                            target_files:
                              - $(WORKSPACE)/publish/back/appsettings.json
                      - name: Upload
                        group: Deploy
                        depends_on: [Substitute]
                        steps:
                          # SECURITY: StrictHostKeyChecking=no + UserKnownHostsFile=/dev/null disable SSH
                          # host-key verification (MITM exposure). Kept for a copy-paste demo template that
                          # has no pre-seeded known_hosts. Before production use, harden to
                          # StrictHostKeyChecking=accept-new (trust-on-first-use) or pre-populate the
                          # target host key in a known_hosts file.
                          - name: Upload via SCP
                            shell: sshpass -e scp -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -P $(TARGET_SSH_PORT) -r $(WORKSPACE)/publish/back/* $(TARGET_USER)@$(TARGET_HOST):$(TARGET_DIR)/
                            timeout_seconds: 180
                      # Steps of one stage run concurrently (ADR-012): each deploy step waits for the previous stage (DEP-008).
                      - name: Restart
                        group: Deploy
                        depends_on: [Upload]
                        steps:
                          - name: Restart Service
                            shell: sshpass -e ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -p $(TARGET_SSH_PORT) $(TARGET_USER)@$(TARGET_HOST) "net stop AetheusBack & net start AetheusBack"
                            timeout_seconds: 60
                    """
            }
        );

        foreach (var entry in db.ChangeTracker.Entries<PipelineTemplate>()
                     .Where(entry => entry.State == EntityState.Added))
        {
            entry.Entity.OrganizationId = defaultOrganization.Id;
        }

        await db.SaveChangesAsync();
    }
}
