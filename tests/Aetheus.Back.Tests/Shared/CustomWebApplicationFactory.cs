// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.Tests;

public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"TestDb_{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // The application defaults include synchronous file logging and, on Windows, the Event Log.
        // Controller tests verify HTTP behavior rather than host logging, so retaining those providers
        // adds serialized disk I/O and can require administrative Event Log access in local runners.
        builder.ConfigureLogging(logging => logging.ClearProviders());
        // UseSetting is visible while Program.cs executes its top-level statements. A later
        // ConfigureAppConfiguration callback cannot satisfy the startup guards in a clean clone.
        builder.UseSetting(
            "ConnectionStrings:Default",
            "Host=test;Database=test;Username=test;Password=test");
        builder.UseSetting("Auth:JwtKey", "aetheus-dev-key-minimum-32-bytes!!");
        builder.UseSetting("Auth:EncryptionKey", "aetheus-encryption-key-32ch!");
        builder.UseSetting("Auth:AdminPassword", "admin");
        builder.UseSetting("RateLimiting:Disabled", "true");
        // Recette R3-7: no nightly AI model list read in a test host (seeded profiles hold fake keys).
        builder.UseSetting("AiTasks:ModelListRefresh:Enabled", "false");
        // Every factory owns an isolated in-memory database, but demo Git repositories live
        // on disk. Disable opt-in startup seeders so parallel controller fixtures never race
        // on the same Toto bare repository; dedicated seeder tests invoke them explicitly.
        builder.UseSetting("Seed:Demo", "false");
        builder.ConfigureServices(services =>
        {
            var toRemove = services.Where(d =>
                d.ServiceType.FullName?.Contains("EntityFramework") == true ||
                d.ServiceType.FullName?.Contains("Npgsql") == true ||
                d.ImplementationType?.FullName?.Contains("EntityFramework") == true ||
                d.ImplementationType?.FullName?.Contains("Npgsql") == true ||
                d.ServiceType == typeof(DbContextOptions<AppDbContext>) ||
                d.ServiceType == typeof(DbContextOptions) ||
                d.ServiceType == typeof(AppDbContext))
                .ToList();
            foreach (var d in toRemove) services.Remove(d);

            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));
        });
    }

    public static string GenerateTestToken() => TestTokenFactory.AdminToken();
}
