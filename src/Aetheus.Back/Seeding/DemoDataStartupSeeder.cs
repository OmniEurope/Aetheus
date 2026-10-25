// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Microsoft.Extensions.Hosting;

namespace Aetheus.Back.Seeding;

internal static class DemoDataStartupSeeder
{
    internal static async Task SeedIfEnabledAsync(
        IServiceProvider services,
        AppDbContext db,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        var enabled = configuration.GetValue("Seed:Demo", false);
        var tier = configuration["Aetheus:EnvironmentTier"];
        var allowed = !environment.IsProduction()
            || string.Equals(tier, "qa", StringComparison.OrdinalIgnoreCase);
        if (!enabled || !allowed) return;

        var seed = await DemoDataSeeder.SeedDemoAsync(
            db, services.GetRequiredService<TimeProvider>(), configuration["Seed:DemoDate"]);
        if (seed is not null)
            await services.GetRequiredService<DemoContentSeeder>().SeedAsync(seed);
    }
}
