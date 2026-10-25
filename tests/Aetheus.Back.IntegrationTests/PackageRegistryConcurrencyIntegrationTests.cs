// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class PackageRegistryConcurrencyIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    [Fact]
    public async Task ConcurrentDbContexts_CannotPublishDuplicatePackageIdentity()
    {
        await ResetAndMigrateAsync();
        await using var first = NewContext();
        await using var second = NewContext();
        first.RegistryPackages.Add(NewPackage());
        second.RegistryPackages.Add(NewPackage());

        var results = await Task.WhenAll(TrySaveAsync(first), TrySaveAsync(second));

        Assert.Single(results, saved => saved);
        await using var verify = NewContext();
        Assert.Equal(
            1,
            await verify.RegistryPackages.CountAsync(
                package => package.Kind == PackageRegistryKind.NuGet
                    && package.NormalizedName == "aetheus.concurrent",
                TestContext.Current.CancellationToken));
    }

    // R1-18 against the real schema: the migration adds IsDeprecated, and it round-trips per version
    // through the repository the NuGet registration reads.
    [Fact]
    public async Task Deprecation_IsStoredPerVersion_AndReadByTheNuGetRegistration()
    {
        await ResetAndMigrateAsync();
        await using (var seed = NewContext())
        {
            var package = NewPackage();
            package.Versions =
            [
                NewVersion("1.0.0"),
                NewVersion("2.0.0")
            ];
            seed.RegistryPackages.Add(package);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var update = NewContext())
        {
            var package = await update.RegistryPackages.Include(item => item.Versions)
                .SingleAsync(TestContext.Current.CancellationToken);
            package.Versions.Single(version => version.Version == "1.0.0").IsDeprecated = true;
            await update.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = NewContext();
        var registration = await new Aetheus.Back.Components.PackageRegistry.PackageRegistryRepository(read)
            .GetRegistrationPackageAsync(
                PackageRegistryKind.NuGet, "aetheus.concurrent", ct: TestContext.Current.CancellationToken);
        Assert.NotNull(registration);
        Assert.Equal(
            ["1.0.0"],
            registration.Versions.Where(version => version.IsDeprecated).Select(version => version.Version));
        Assert.Equal(2, registration.Versions.Count);
    }

    private static RegistryPackageVersion NewVersion(string version) => new()
    {
        Version = version,
        NormalizedVersion = version,
        FilePath = $"nuget/{version}.nupkg",
        ContentType = "application/octet-stream",
        Sha256 = new string('a', 64),
        Sha1 = new string('b', 40),
        Integrity = "sha512-x",
        Metadata = "{}",
        PublishedBy = "tester",
        CreatedAt = DateTime.UtcNow
    };

    private static RegistryPackage NewPackage()
    {
        var now = DateTime.UtcNow;
        return new RegistryPackage
        {
            Kind = PackageRegistryKind.NuGet,
            Name = "Aetheus.Concurrent",
            NormalizedName = "aetheus.concurrent",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static async Task<bool> TrySaveAsync(Aetheus.Back.Data.AppDbContext db)
    {
        try
        {
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }
}
