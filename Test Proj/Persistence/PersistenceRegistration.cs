using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;
using Test_Proj.Services.Sync;

namespace Test_Proj.Persistence;

public sealed class DatabaseConfigurationException() : Exception("Configure the database connection through trusted configuration.");
public sealed class DatabaseOperationException() : Exception("Database operation failed.");
public sealed class DatabaseTimeoutException() : Exception("Database operation timed out.");

public static class PersistenceRegistration
{
    public static IServiceCollection AddPolicePersistence(this IServiceCollection services, IConfiguration configuration)
    {
        // Lazy validation allows existing CSV endpoints to run without a database.
        services.AddDbContext<PoliceDbContext>(options => Configure(options, configuration.GetConnectionString("DefaultConnection")));
        services.AddScoped<PoliceSyncService>();
        return services;
    }

    public static void Configure(DbContextOptionsBuilder options, string? connection)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(connection)) throw new DatabaseConfigurationException();
            var parsed = new NpgsqlConnectionStringBuilder(connection);
            if (string.IsNullOrWhiteSpace(parsed.Host) || string.IsNullOrWhiteSpace(parsed.Database))
                throw new DatabaseConfigurationException();
            parsed.IncludeErrorDetail = false;
            parsed.LogParameters = false;
            parsed.Timeout = Math.Clamp(parsed.Timeout, 1, 15);
            parsed.CommandTimeout = Math.Clamp(parsed.CommandTimeout, 1, 30);
            options.UseNpgsql(parsed.ConnectionString)
                .EnableSensitiveDataLogging(false).EnableDetailedErrors(false);
            // EF logs can include command text, connection details and exception objects even without sensitive data logging.
            options.UseLoggerFactory(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        }
        catch (DatabaseConfigurationException) { throw; }
        catch (Exception) { throw new DatabaseConfigurationException(); }
    }
}

// Does not start the web host, load local files, or read User Secrets. EF commands use environment configuration only.
public sealed class PoliceDbContextFactory : IDesignTimeDbContextFactory<PoliceDbContext>
{
    public PoliceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PoliceDbContext>();
        if (args.Contains("--schema-only")) return new(options.UseNpgsql().Options);
        PersistenceRegistration.Configure(options, Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection"));
        return new(options.Options);
    }
}
