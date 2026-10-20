// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Hosts the real <see cref="Program"/> pipeline against a Testcontainers PostgreSQL instance.
/// <para>
/// Unlike <c>Aetheus.Back.Tests/CustomWebApplicationFactory.cs</c> - which removes the relational
/// provider and swaps in EF InMemory - this factory keeps the production Npgsql provider and only
/// repoints <c>ConnectionStrings:Default</c> at the container. As a result the app runs
/// <c>db.Database.MigrateAsync()</c> + <c>DbInitializer.SeedAsync()</c> on a genuine Postgres on
/// startup, exactly like production.
/// </para>
/// </summary>
public sealed class AetheusWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "Integr@tion-Test-Admin-Pwd-2026";

    private readonly string _connectionString;
    private readonly string _testDataRoot = Path.Combine(
        Path.GetTempPath(), "aetheus-integration", Guid.NewGuid().ToString("N"));

    /// <param name="resetSchema">DRPS: when <c>true</c> (default - preserves the historical behaviour)
    /// the public schema is dropped and recreated so Program.cs MigrateAsync + SeedAsync run against a
    /// pristine DB. A suite that owns a freshly-provisioned container, or that has already reset the
    /// schema once at fixture init, passes <c>false</c> to skip the per-host wipe - the prerequisite for
    /// collapsing <see cref="ApiSmokeFixture"/> and <see cref="PostgresFixture"/> onto a single shared
    /// (assembly-level) Postgres container instead of booting two concurrently. (The container-merge
    /// itself is a follow-up that must be validated under Docker to avoid reintroducing cross-suite flake;
    /// this opt-in is the foundational, behaviour-preserving step.)</param>
    public AetheusWebApplicationFactory(string connectionString, bool resetSchema = true)
    {
        _connectionString = connectionString;

        // Drop and re-create the public schema so that Program.cs MigrateAsync + SeedAsync always run
        // against a pristine database. Without this, tests in the same Postgres collection that seed
        // their own data (e.g. DbInitializerIntegrationTests) can leave stale rows that cause SeedAsync
        // to skip - seeding with a different admin password than the one the HTTP tests expect.
        if (!resetSchema) return;

        PostgresDatabaseTemplate.Reset(connectionString);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Select the current checkout's backend root explicitly. QA also rebuilds this test assembly
        // after restoring CI outputs because ASP.NET's static-web-assets manifest embeds the absolute
        // build workspace before ConfigureWebHost runs.
        builder.UseContentRoot(Path.Combine(FindRepoRoot(), "src", "Aetheus.Back"));

        // Development => Program.cs falls back to a dev JWT key and maps OpenAPI.
        builder.UseEnvironment("Development");

        // UseSetting applies to the WebApplicationBuilder.Configuration immediately, so
        // Program.cs reads the correct values during top-level statements (before Build()).
        // ConfigureAppConfiguration alone is too late for minimal API programs - the callbacks
        // replay AFTER builder.Configuration is already consumed by the application code.
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Auth:JwtKey", "aetheus-integration-test-jwt-key-0123456789!!");
        builder.UseSetting("Auth:EncryptionKey", "aetheus-integration-test-encryption-key-0123456789");
        // Must be valid base64 decoding to >= 16 bytes (EncryptionService.GetConfiguredSalt validates).
        builder.UseSetting("Auth:EncryptionSalt", "aW50ZWdyYXRpb24tc2FsdC0xNi1ieXRlcy1taW4h");
        builder.UseSetting("Auth:AdminPassword", AdminPassword);
        builder.UseSetting("RateLimiting:Disabled", "true");
        // Recette R3-7: no nightly AI model list read in a test host (seeded profiles hold fake keys).
        builder.UseSetting("AiTasks:ModelListRefresh:Enabled", "false");
        // Every factory may seed the same demo repository concurrently. Never share the default
        // bin/.../data paths between hosts or test runs: a stale bare Git ref otherwise makes a
        // fresh database seed fail with "reference already exists".
        builder.UseSetting("GitLight:RepositoriesPath", Path.Combine(_testDataRoot, "git-repos"));
        builder.UseSetting("ArtifactStorage:BasePath", Path.Combine(_testDataRoot, "artifacts"));
        // Development appsettings enables the rich demo dataset for humans. Integration factories
        // start many real hosts against shared test storage; allowing every host to seed the same
        // Toto bare repository races its refs/hooks and makes the suite nondeterministic. Demo seeding
        // has dedicated relational tests, so the generic factory must keep startup seeding disabled.
        builder.UseSetting("Seed:Demo", "false");
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Aetheus.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found for the integration content root.");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || !Directory.Exists(_testDataRoot)) return;

        try
        {
            Directory.Delete(_testDataRoot, recursive: true);
        }
        catch (IOException)
        {
            // Testcontainers/host shutdown can briefly retain a handle. The GUID-scoped temp root
            // remains isolated and cannot affect a later run even when best-effort cleanup loses.
        }
        catch (UnauthorizedAccessException)
        {
            // Same isolation guarantee as above; never turn a successful integration test red only
            // because the operating system delayed releasing a temporary Git file.
        }
    }
}
