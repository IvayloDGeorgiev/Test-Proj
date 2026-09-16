using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Test_Proj.Persistence;

namespace PoliceDataIngestion.Api.Tests.Persistence;

// A separate, ephemeral loopback cluster: never connects to the operator's PostgreSQL instance.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string sandbox = Path.Combine(Path.GetTempPath(), "PolicePostgresTests", Guid.NewGuid().ToString("N"));
    private readonly string binaries = Environment.GetEnvironmentVariable("POLICE_TEST_PG_BIN") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL", "16", "bin");
    private int port;
    private bool started;

    public async Task InitializeAsync()
    {
        if (!File.Exists(Path.Combine(binaries, "initdb.exe")))
            throw new InvalidOperationException("PostgreSQL test binaries required. Set POLICE_TEST_PG_BIN to their bin directory.");
        Directory.CreateDirectory(sandbox);
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        await Run("initdb", "-D", Path.Combine(sandbox, "data"), "--auth=trust", "--username=stage9_test", "--encoding=UTF8", "--no-locale");
        await Run("pg_ctl", "-D", Path.Combine(sandbox, "data"), "-l", Path.Combine(sandbox, "server.log"),
            "-o", $"-h 127.0.0.1 -p {port}", "-w", "start");
        started = true;
    }

    public string Connection(string database) => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1", Port = port, Database = database, Username = "stage9_test", Pooling = false,
        Timeout = 3, CommandTimeout = 5, IncludeErrorDetail = false, LogParameters = false
    }.ConnectionString;

    public async Task<string> CreateDatabaseAsync(bool migrate = true)
    {
        var name = "test_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(Connection("postgres"));
        await admin.OpenAsync();
        // Identifier is generated exclusively above; application tables are created only by EF migrations.
        await using var command = new NpgsqlCommand("CREATE DATABASE " + name, admin);
        await command.ExecuteNonQueryAsync();
        var connection = Connection(name);
        if (migrate) { await using var db = Context(connection); await db.Database.MigrateAsync(); }
        return connection;
    }

    public static PoliceDbContext Context(string connection)
    {
        var options = new DbContextOptionsBuilder<PoliceDbContext>();
        PersistenceRegistration.Configure(options, connection);
        return new(options.Options);
    }

    private async Task Run(string name, params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(binaries, name + ".exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start isolated PostgreSQL.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        // pg_ctl descendants may inherit redirected handles on Windows after pg_ctl exits.
        // Output is deliberately not printed; bound draining instead of waiting for the server lifetime.
        await Task.WhenAny(Task.WhenAll(output, error), Task.Delay(TimeSpan.FromSeconds(1)));
        process.StandardOutput.Close();
        process.StandardError.Close();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Isolated PostgreSQL {name} failed (exit {process.ExitCode}).");
    }

    public async Task DisposeAsync()
    {
        if (started) await Run("pg_ctl", "-D", Path.Combine(sandbox, "data"), "-m", "fast", "-w", "stop");
        var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PolicePostgresTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(sandbox).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test sandbox.");
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
    }
}
