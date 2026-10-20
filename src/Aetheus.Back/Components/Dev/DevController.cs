// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Dev;

/// <summary>
/// Dev-only helpers for test data. Every endpoint 404s when the host is NOT running in the
/// Development environment, so this controller is safe to register globally - it simply
/// refuses to act in prod.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DevController(
    AppDbContext db,
    IWebHostEnvironment env,
    IConfiguration config,
    IMemoryCache cache,
    TimeProvider timeProvider,
    DemoContentSeeder demoContentSeeder,
    SchemaResetGate resetGate) : ControllerBase
{
    /// <summary>
    /// F-035: Recreates the isolated E2E schema, applies every EF migration and re-seeds. E2E tests
    /// call this in [OneTimeSetUp] so each suite starts from a known state without relying on the
    /// reversibility of an older local migration that may have changed during development.
    /// </summary>
    [HttpPost("reset-db")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> ResetDatabase(CancellationToken ct)
    {
        if (!env.IsDevelopment()) return NotFound();

        // This endpoint drops the entire database and is restricted to isolated, disposable E2E
        // sandboxes. Refuse any connected database whose name does not end in _e2e, protecting
        // shared development and production databases from a misconfigured connection string.
        var connectedDb = db.Database.GetDbConnection().Database ?? string.Empty;
        if (!connectedDb.EndsWith("_e2e", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(
                detail: $"reset-db refused: connected database '{connectedDb}' is not an isolated E2E "
                    + "sandbox (its name must end with '_e2e'). Refusing to drop a non-E2E database.",
                statusCode: 403);
        }

        // Keep the database and connection target alive, but rebuild its disposable schema from a
        // blank state. Migrating down is not a reliable reset for a long-lived local E2E database:
        // an in-development migration can have been applied before its final Down shape existed,
        // leaving history and physical columns inconsistent. The database-name guard above is the
        // authorization boundary for this destructive schema operation.
        // The gate holds every other database command (background services polling) until the
        // schema is rebuilt and seeded, so none of them meets a half-migrated schema.
        await resetGate.RunClosedAsync(async () =>
        {
            await db.Database.ExecuteSqlRawAsync(
                "DROP SCHEMA public CASCADE; CREATE SCHEMA public;", ct).ConfigureAwait(false);
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
            // Pass config so DbInitializer seeds the admin USER (it requires Auth:AdminPassword;
            // without config the seed throws and the DB is left user-less, forcing the bootstrap
            // login path whose token has no DB user - which 404s GET api/users/me/permissions and
            // strands every permission-gated button as disabled). Mirrors Program.cs startup seeding.
            await DbInitializer.SeedAsync(db, config).ConfigureAwait(false);
            if (config.GetValue("Seed:Demo", false))
            {
                var seed = await DemoDataSeeder.SeedDemoAsync(db, timeProvider).ConfigureAwait(false);
                if (seed is not null)
                    await demoContentSeeder.SeedAsync(seed, ct).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);

        // The DB was wiped and re-seeded out-of-band, so any cached DB-derived state is stale.
        // Critically, the security-stamp cache (sec-stamp:{userId}, 30s TTL) - populated by THIS
        // request's own [Authorize] validation with the PRE-reset admin's stamp - would otherwise
        // reject freshly-issued tokens for the NEW admin until it expires (401 → login bounce).
        // Same applies to the authz role/org caches. Clear everything: the source of truth is gone.
        if (cache is MemoryCache memoryCache)
            memoryCache.Clear();

        return Ok(new { Message = "Database reset and re-seeded." });
    }
}
