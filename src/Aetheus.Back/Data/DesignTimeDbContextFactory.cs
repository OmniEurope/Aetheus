// SPDX-License-Identifier: EUPL-1.2
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Aetheus.Back.Data;

/// <summary>
/// Used by the EF Core CLI (`dotnet ef migrations`) only. Resolves the connection string from
/// an explicitly forwarded design-time argument or the standard ASP.NET Core configuration sources
/// (environment, appsettings.Development.json, user-secrets) to avoid hard-coding credentials in source.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>
    /// Command timeout of the migration bundle. Npgsql's 30-second default cut the production
    /// deploy #152 on an index build over the vulnerability feed tables, which only lasts that
    /// long on production-sized data. Stays below the 3600-second migrate step timeout.
    /// </summary>
    internal static readonly TimeSpan MigrationCommandTimeout = TimeSpan.FromMinutes(30);

    public AppDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .AddUserSecrets<DesignTimeDbContextFactory>(optional: true)
            .Build();

        var connectionString = ResolveConnectionString(configuration, args);

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsql => npgsql.CommandTimeout((int)MigrationCommandTimeout.TotalSeconds));
        return new AppDbContext(optionsBuilder.Options);
    }

    internal static string ResolveConnectionString(
        IConfiguration configuration,
        IReadOnlyList<string>? args = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = GetDesignConnectionArgument(args)
            ?? configuration.GetConnectionString("Default")
            ?? configuration["AETHEUS_DESIGN_CONNECTION"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Design-time database connection is not configured. Pass --design-connection after --, " +
                "or set ConnectionStrings:Default or AETHEUS_DESIGN_CONNECTION before running dotnet ef.");
        return connectionString;
    }

    private static string? GetDesignConnectionArgument(IReadOnlyList<string>? args)
    {
        if (args is null)
            return null;

        const string option = "--design-connection";
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.StartsWith($"{option}=", StringComparison.Ordinal))
                return argument[(option.Length + 1)..];

            if (string.Equals(argument, option, StringComparison.Ordinal) && index + 1 < args.Count)
                return args[index + 1];
        }

        return null;
    }
}
