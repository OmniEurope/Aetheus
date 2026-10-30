// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Tests.Architecture;

public sealed class E2eDatabaseIsolationAuditTests
{
    [Fact]
    public void Launchers_PreserveTheDedicatedE2eVolume()
    {
        var root = RepoRoot();
        var windowsLauncher = File.ReadAllText(Path.Combine(root, "scripts", "launch-core.ps1"));
        var linuxLauncher = File.ReadAllText(Path.Combine(root, "launch-linux.sh"));

        Assert.Contains("Ensure-E2eDb", windowsLauncher, StringComparison.Ordinal);
        Assert.Contains(
            "BackgroundServices__TaskStartupDelay = \"00:30:00\"",
            windowsLauncher,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Reset-E2eDb", windowsLauncher, StringComparison.Ordinal);
        Assert.DoesNotContain("docker compose -f $e2eDbCompose down -v", windowsLauncher, StringComparison.Ordinal);
        Assert.Contains("ensure_e2e_db", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("front_dev_settings=", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("https://localhost:5301", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("BackgroundServices__TaskStartupDelay=\"00:30:00\"", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("E2E_FRONTEND_URL=\"https://localhost:5401\"", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("E2E_BACKEND_URL=\"https://localhost:5301\"", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("E2E_REQUIRE_FRONTEND_SECURITY_HEADERS=\"false\"", linuxLauncher, StringComparison.Ordinal);
        Assert.DoesNotContain("reset_e2e_db", linuxLauncher, StringComparison.Ordinal);
        Assert.DoesNotContain("docker compose -f \"$E2E_DB_COMPOSE\" down -v", linuxLauncher, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxLauncher_InstallsItsBuiltPlaywrightChromiumBeforeE2e()
    {
        var launcher = File.ReadAllText(Path.Combine(RepoRoot(), "launch-linux.sh"));

        Assert.Contains(".playwright/package/cli.js", launcher, StringComparison.Ordinal);
        Assert.Contains("install --with-deps chromium", launcher, StringComparison.Ordinal);
        Assert.True(
            launcher.IndexOf("if ! ensure_playwright_browser", StringComparison.Ordinal)
            < launcher.LastIndexOf("    ensure_e2e_db", StringComparison.Ordinal));
    }

    [Fact]
    public void ComposeAndBackendReset_RemainIsolatedAndFailClosed()
    {
        var root = RepoRoot();
        var compose = File.ReadAllText(Path.Combine(root, "deploy", "compose", "e2e-db.compose.yml"));
        var fixture = File.ReadAllText(Path.Combine(root, "tests", "Aetheus.E2E", "E2EGlobalSetup.cs"));
        var baseFixture = File.ReadAllText(Path.Combine(root, "tests", "Aetheus.E2E", "E2ETestBase.cs"));
        var controller = File.ReadAllText(Path.Combine(
            root, "src", "Aetheus.Back", "Components", "Dev", "DevController.cs"));

        Assert.Contains("name: aetheus-e2e", compose, StringComparison.Ordinal);
        Assert.Contains("container_name: aetheus-e2e-database", compose, StringComparison.Ordinal);
        Assert.Contains("15433:5432", compose, StringComparison.Ordinal);
        Assert.Contains("POSTGRES_DB=aetheus_e2e", compose, StringComparison.Ordinal);
        Assert.Contains("aetheus-e2e-db-data:/var/lib/postgresql/data", compose, StringComparison.Ordinal);
        Assert.Contains("response.EnsureSuccessStatusCode()", fixture, StringComparison.Ordinal);
        Assert.Contains("resetAttempt <= 2", fixture, StringComparison.Ordinal);
        Assert.Contains("!resetResponse.IsSuccessStatusCode", fixture, StringComparison.Ordinal);
        Assert.Contains("ReadAsStringAsync", fixture, StringComparison.Ordinal);
        Assert.Contains("EndsWith(\"_e2e\"", controller, StringComparison.Ordinal);
        Assert.Contains("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", controller, StringComparison.Ordinal);
        Assert.Contains("db.Database.MigrateAsync(ct)", controller, StringComparison.Ordinal);
        Assert.Contains("StorageStateJson", baseFixture, StringComparison.Ordinal);
        Assert.Contains("aetheus_auth_token", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteCompose_DefaultsToProduction()
    {
        var remoteCompose = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "compose", "remote.compose.yml"));

        Assert.Contains("ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT:-Production}", remoteCompose, StringComparison.Ordinal);
        Assert.Contains("Database=${DB_NAME:-aetheus}", remoteCompose, StringComparison.Ordinal);
        Assert.Contains("POSTGRES_DB=${DB_NAME:-aetheus}", remoteCompose, StringComparison.Ordinal);
        Assert.Contains("BackgroundServices__ServerHeartbeatTimeout=${SERVER_HEARTBEAT_TIMEOUT:-00:02:00}", remoteCompose, StringComparison.Ordinal);
        Assert.Contains("pg_isready -U ${DB_USER} -d ${DB_NAME:-aetheus}", remoteCompose, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationFactory_DisablesConcurrentDemoContentStartupSeeding()
    {
        var factory = File.ReadAllText(Path.Combine(
            RepoRoot(), "tests", "Aetheus.Back.IntegrationTests", "AetheusWebApplicationFactory.cs"));

        Assert.Contains("builder.UseSetting(\"Seed:Demo\", \"false\")", factory, StringComparison.Ordinal);
        Assert.Contains("builder.UseContentRoot", factory, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrationDatabase_UsesAMigratedTemplateInsteadOfReplayingMigrationsPerTest()
    {
        var root = RepoRoot();
        var template = File.ReadAllText(Path.Combine(
            root, "tests", "Aetheus.Back.IntegrationTests", "PostgresDatabaseTemplate.cs"));
        var relationalBase = File.ReadAllText(Path.Combine(
            root, "tests", "Aetheus.Back.IntegrationTests", "RelationalTestBase.cs"));

        Assert.Contains("CREATE DATABASE {database} TEMPLATE", template, StringComparison.Ordinal);
        Assert.Contains("await db.Database.MigrateAsync()", template, StringComparison.Ordinal);
        Assert.Contains("await fixture.ResetAsync()", relationalBase, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP SCHEMA public CASCADE", relationalBase, StringComparison.Ordinal);
    }

    [Fact]
    public void Launchers_PropagateTheCombinedTestResult()
    {
        var root = RepoRoot();
        var entryPoint = File.ReadAllText(Path.Combine(root, "launch-windows.ps1"));
        var core = File.ReadAllText(Path.Combine(root, "scripts", "launch-core.ps1")).ReplaceLineEndings("\n");
        var linuxLauncher = File.ReadAllText(Path.Combine(root, "launch-linux.sh"));

        Assert.Contains("$script:ylaunchExitCode = 0", entryPoint, StringComparison.Ordinal);
        Assert.Contains("exit $script:ylaunchExitCode", entryPoint, StringComparison.Ordinal);
        Assert.Contains("$script:ylaunchExitCode = 1", core, StringComparison.Ordinal);
        // A failed suite is named (its Failure text), stops the stages that follow and fails the run through
        // the combined summary below, never through a bare exit.
        Assert.Contains("Failure = \"INTEGRATION TESTS FAILED\"", core, StringComparison.Ordinal);
        Assert.Contains("if ($phase.Failed.Count -gt 0) { $testsAborted = $true }", core, StringComparison.Ordinal);
        Assert.Contains("if ($TestE2e -and -not $testsAborted)", core, StringComparison.Ordinal);
        Assert.Matches(@"SOME TESTS FAILED\.""\s+-ForegroundColor Red\s+\$script:ylaunchExitCode = 1\s+return", core);
        Assert.DoesNotContain("TESTS FAILED - aborting.\" -ForegroundColor Red; exit 1", core, StringComparison.Ordinal);
        Assert.Contains("$processFailures.Count -gt 0", core, StringComparison.Ordinal);
        Assert.Contains("throw [System.ArgumentException]::new(\"Unknown E2E category", core, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown category: '$part'\" -ForegroundColor Red\r\n            exit 1", core, StringComparison.Ordinal);
        Assert.Contains("$e2eOnly", core, StringComparison.Ordinal);
        Assert.Contains("Linux agent publish skipped for test-only execution", core, StringComparison.Ordinal);
        Assert.Contains("$_.Total -le 0", core, StringComparison.Ordinal);
        Assert.Contains("$trxDocument.TestRun.ResultSummary.Counters", core, StringComparison.Ordinal);
        Assert.Contains("$expectedTotal = $trxTotal", core, StringComparison.Ordinal);
        Assert.Contains("docker compose up failed.\" -ForegroundColor Red", core, StringComparison.Ordinal);
        Assert.Contains(
            "'{\"expirationHours\":1}'",
            core,
            StringComparison.Ordinal);
        Assert.Contains("if [[ $total_failed -gt 0 ]]; then", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("TEST_PROCESS_FAILED=1", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("E2E_ONLY=0", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("$total_all -le 0", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("SOME TESTS FAILED", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("exit 1", linuxLauncher, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsLauncher_StopsListenersBeforeDisposingTheirParentJobs()
    {
        var launcher = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "launch-core.ps1"));
        var stopServersStart = launcher.IndexOf("function Stop-Servers", StringComparison.Ordinal);
        var stopServersEnd = launcher.IndexOf(
            "# ============================================================",
            stopServersStart,
            StringComparison.Ordinal);
        Assert.True(stopServersStart >= 0 && stopServersEnd > stopServersStart);

        var stopServers = launcher[stopServersStart..stopServersEnd];
        var listenerStop = stopServers.IndexOf(
            "Stop-AetheusProcesses -ListenersOnly",
            StringComparison.Ordinal);
        var jobStop = stopServers.IndexOf(
            "Stop-Job -Job $jobs[$key]",
            StringComparison.Ordinal);

        Assert.True(listenerStop >= 0);
        Assert.True(jobStop > listenerStop);
    }

    [Fact]
    public void WindowsLauncher_SharesDatabaseBackedFilesAcrossCoexistingWorktrees()
    {
        var launcher = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "launch-core.ps1"));

        Assert.Contains("Aetheus\\shared-development", launcher, StringComparison.Ordinal);
        Assert.Contains("$env:ArtifactStorage__BasePath", launcher, StringComparison.Ordinal);
        Assert.Contains("$env:PackageRegistry__BasePath", launcher, StringComparison.Ordinal);
        Assert.Contains("$env:GitLight__RepositoriesPath", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void R3_7_E2eAndQaHosts_SwitchOffTheNightlyAiModelListRead_ProductionKeepsIt()
    {
        // Recette R3-7: the E2E backends (both ylaunch branches, ybaunch) and the QA stack hold seeded
        // profiles with fake keys, and the nightly QA runs inside the 01:00-05:00 UTC read window. The QA
        // stack sets AI_MODEL_LIST_REFRESH_ENABLED=false in its pipeline definitions, which tests do not read
        // (AGENTS.md); the compose default below is what that variable switches.
        var root = RepoRoot();
        var windowsLauncher = File.ReadAllText(Path.Combine(root, "scripts", "launch-core.ps1"));
        var linuxLauncher = File.ReadAllText(Path.Combine(root, "launch-linux.sh"));
        var remoteCompose = File.ReadAllText(Path.Combine(root, "deploy", "compose", "remote.compose.yml"));

        Assert.Contains("$env:AiTasks__ModelListRefresh__Enabled = \"false\"", windowsLauncher, StringComparison.Ordinal);
        Assert.Contains("export AiTasks__ModelListRefresh__Enabled=\"false\"", linuxLauncher, StringComparison.Ordinal);
        Assert.Contains("AiTasks__ModelListRefresh__Enabled=${AI_MODEL_LIST_REFRESH_ENABLED:-true}", remoteCompose, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Aetheus.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
