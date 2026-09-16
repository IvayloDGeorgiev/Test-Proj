using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoliceDataIngestion.Api.Tests.Integration;
using Test_Proj.Clients.PoliceApi;
using Test_Proj.Contracts.Requests;
using Test_Proj.Errors;
using Test_Proj.Export;
using Test_Proj.Persistence;
using Test_Proj.Services.Sync;
using Test_Proj.Validation;

namespace PoliceDataIngestion.Api.Tests.Persistence;

public sealed class PersistenceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly LocationMonthRequest Query = new(53.8, -1.5, "2024-01");

    [Fact]
    public async Task Migrations_EmptyDatabase_AppliesIdempotentlyAndEnforcesConstraints()
    {
        // Arrange
        var connection = await postgres.CreateDatabaseAsync(false);
        await using var db = PostgresFixture.Context(connection);
        Assert.Equal(2, (await db.Database.GetPendingMigrationsAsync()).Count());
        // Act
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();
        // Assert
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(0, await db.Records.CountAsync());
        var now = DateTimeOffset.UtcNow;
        db.Records.Add(new() { Dataset = "invalid", Scope = "all", Key = "one", Data = "{}", FirstSeenUtc = now, UpdatedAtUtc = now, LastSeenUtc = now });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        db.ChangeTracker.Clear();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("0");
        Assert.Equal(2, (await db.Database.GetPendingMigrationsAsync()).Count());
        await db.Database.MigrateAsync();
        Assert.Equal(0, await db.Records.CountAsync());
    }

    [Fact]
    public async Task Forces_InsertRepeatUpdateEmpty_PreservesOneRecordAndMetadata()
    {
        // Arrange
        await using var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        var client = new FakeClient { Forces = [new() { Id = "one", Name = "Original" }] };
        var service = new PoliceSyncService(db, client, new(), TimeProvider.System);
        // Act
        var first = await service.SyncAsync("forces", null, default);
        var original = await db.Records.AsNoTracking().SingleAsync();
        var repeat = await service.SyncAsync("forces", null, default);
        client.Forces = [new() { Id = "one", Name = "Changed" }];
        var update = await service.SyncAsync("forces", null, default);
        client.Forces = [];
        var empty = await service.SyncAsync("forces", null, default);
        var row = await db.Records.AsNoTracking().SingleAsync();
        // Assert
        Assert.Equal(1, first.Inserted); Assert.Equal(1, repeat.Unchanged); Assert.Equal(0, repeat.Inserted);
        Assert.Equal(1, update.Updated); Assert.Equal(0, update.Inserted); Assert.Equal(0, empty.Received);
        Assert.Contains("Changed", row.Data); Assert.Equal(original.FirstSeenUtc, row.FirstSeenUtc);
        Assert.True(row.LastSeenUtc >= row.UpdatedAtUtc);
    }

    [Fact]
    public async Task Crimes_PersistentIdSurvivesNumericIdChange_UpdatesWithoutDuplicate()
    {
        // Arrange
        await using var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        var value = new CrimeDto { Id = 1, PersistentId = "stable", Category = "burglary", Month = "2024-01" };
        var client = new FakeClient { Crimes = [value, value] };
        var service = new PoliceSyncService(db, client, new(), TimeProvider.System);
        // Act
        var first = await service.SyncAsync("crimes", Query, default);
        client.Crimes = [value with { Id = 2, Outcome = new() { Category = "Updated" } }];
        var second = await service.SyncAsync("crimes", Query, default);
        var page = await service.ReadAsync("crimes", Query, 0, 50, default);
        // Assert
        Assert.Equal(1, first.Inserted); Assert.Equal(1, second.Updated);
        Assert.Equal(2, Assert.Single(page.Items).Data.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Stops_ReorderedIdenticalEventsAndCorrection_ReconcilesSnapshotAndEmpty()
    {
        // Arrange
        await using var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        var value = new StopSearchDto { Type = "Person search", Datetime = "2024-01-01T12:00:00+00:00" };
        var other = value with { Type = "Vehicle search" };
        var client = new FakeClient { Stops = [value, value, other] };
        var service = new PoliceSyncService(db, client, new(), TimeProvider.System);
        // Act
        var first = await service.SyncAsync("stop-searches", Query, default);
        client.Stops = [other, value, value];
        var repeated = await service.SyncAsync("stop-searches", Query, default);
        client.Stops = [value with { Outcome = "Corrected" }];
        var corrected = await service.SyncAsync("stop-searches", Query, default);
        var page = await service.ReadAsync("stop-searches", Query, 0, 50, default);
        client.Stops = [];
        var empty = await service.SyncAsync("stop-searches", Query, default);
        // Assert
        Assert.Equal(3, first.Inserted); Assert.Equal(3, repeated.Unchanged); Assert.Equal(0, repeated.Inserted);
        Assert.Equal(3, corrected.Removed); Assert.Equal(1, corrected.Inserted);
        Assert.Equal("Corrected", Assert.Single(page.Items).Data.GetProperty("outcome").GetString());
        Assert.Equal(1, empty.Removed); Assert.Empty(await db.Records.ToListAsync());
    }

    [Fact]
    public async Task InvalidPartialUpstream_PreservesExistingDataAndReleasesAdmission()
    {
        // Arrange
        await using var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        var client = new FakeClient { Forces = [new() { Id = "one", Name = "Original" }] };
        var gate = new OperationLease();
        var service = new PoliceSyncService(db, client, gate, TimeProvider.System);
        await service.SyncAsync("forces", null, default);
        client.Forces = [new() { Id = "two", Name = "Valid" }, new() { Id = "invalid" }];
        // Act
        await Assert.ThrowsAsync<PoliceApiException>(() => service.SyncAsync("forces", null, default));
        // Assert
        Assert.Equal("one", (await db.Records.SingleAsync()).Key);
        using var lease = gate.Acquire();
    }

    [Fact]
    public async Task ConcurrentSyncAndCsvGate_RejectsImmediatelyAndRecovers()
    {
        // Arrange
        await using var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        var gate = new OperationLease();
        var client = new FakeClient();
        var service = new PoliceSyncService(db, client, gate, TimeProvider.System);
        // Act / Assert
        using (gate.Acquire())
        {
            var error = await Assert.ThrowsAsync<ExportException>(() => service.SyncAsync("forces", null, default));
            Assert.Equal(409, error.StatusCode); Assert.Equal(0, client.Calls);
        }
        Assert.True((await service.SyncAsync("forces", null, default)).Success);
    }

    [Fact]
    public async Task DatabaseLock_IndependentProcessGate_RejectsSecondWriter()
    {
        // Arrange
        var connection = await postgres.CreateDatabaseAsync();
        await using var locked = new NpgsqlConnection(connection);
        await locked.OpenAsync();
        await using var transaction = await locked.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(90420260916)", locked, transaction);
        await command.ExecuteNonQueryAsync();
        await using var db = PostgresFixture.Context(connection);
        var service = new PoliceSyncService(db, new FakeClient(), new(), TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAsync<ExportException>(() => service.SyncAsync("forces", null, default));
        // Assert
        Assert.Equal(409, error.StatusCode); Assert.Empty(await db.Records.ToListAsync());
        await transaction.RollbackAsync();
        Assert.True((await service.SyncAsync("forces", null, default)).Success);
    }

    [Fact]
    public async Task Host_SyncReadAndPagination_ReturnsPersistedDataAndSafeMetadata()
    {
        // Arrange
        await using var host = new IngestionHost((_, _, _) => Task.FromResult(IngestionHost.Response("[{\"id\":\"a\",\"name\":\"One\"},{\"id\":\"b\",\"name\":\"Two\"}]")));
        host.Settings["ConnectionStrings:DefaultConnection"] = await postgres.CreateDatabaseAsync();
        using var http = host.Client(); http.DefaultRequestHeaders.Add("X-Police-Sync", "1");
        // Act
        var sync = await http.PostAsync("/api/sync/forces", null);
        var result = await sync.Content.ReadFromJsonAsync<SyncResult>();
        var page = await http.GetFromJsonAsync<PersistedPage>("/api/data/forces?limit=1");
        var second = await http.GetFromJsonAsync<PersistedPage>("/api/data/forces?limit=1&offset=1");
        // Assert
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode); Assert.Equal(2, result!.Inserted);
        Assert.True(page!.HasMore); Assert.Equal("a", Assert.Single(page.Items).Key);
        Assert.False(second!.HasMore); Assert.Equal("b", Assert.Single(second.Items).Key);
        Assert.Empty(Directory.Exists(host.OutputRoot) ? Directory.GetFiles(host.OutputRoot) : []);
    }

    [Theory]
    [InlineData("missing", "database_not_configured")]
    [InlineData("invalid", "database_not_configured")]
    [InlineData("unmigrated", "database_unavailable")]
    [InlineData("unavailable", "database_unavailable")]
    public async Task Host_DatabaseFailure_ReturnsSanitized503WithoutConnectionDetails(string mode, string code)
    {
        // Arrange
        await using var host = new IngestionHost();
        if (mode == "invalid") host.Settings["ConnectionStrings:DefaultConnection"] = "synthetic-private-marker";
        if (mode == "unmigrated") host.Settings["ConnectionStrings:DefaultConnection"] = await postgres.CreateDatabaseAsync(false);
        if (mode == "unavailable") host.Settings["ConnectionStrings:DefaultConnection"] = postgres.Connection("missing_" + Guid.NewGuid().ToString("N"));
        using var http = host.Client();
        // Act
        var response = await http.GetAsync("/api/data/forces");
        var body = await response.Content.ReadAsStringAsync();
        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Contains(code, body);
        Assert.DoesNotContain("synthetic-private-marker", body); Assert.DoesNotContain("stage9_test", body);
        Assert.DoesNotContain(host.Logs.Entries, x => x.Exception is not null || x.Message.Contains("stage9_test") || x.Message.Contains("synthetic-private-marker"));
    }

    [Theory]
    [InlineData("/api/data/forces?limit=201")]
    [InlineData("/api/data/forces?offset=-1")]
    [InlineData("/api/data/unknown")]
    [InlineData("/api/data/crimes?latitude=91&longitude=0&month=2024-01")]
    public async Task Host_InvalidRead_Returns400(string path)
    {
        // Arrange
        await using var host = new IngestionHost();
        host.Settings["ConnectionStrings:DefaultConnection"] = await postgres.CreateDatabaseAsync();
        using var http = host.Client();
        // Act
        var response = await http.GetAsync(path);
        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Handler.Calls);
    }

    [Fact]
    public async Task Host_SyncOriginAndInputLimits_RejectBeforeNetwork()
    {
        // Arrange
        await using var host = new IngestionHost();
        host.Settings["ConnectionStrings:DefaultConnection"] = await postgres.CreateDatabaseAsync();
        using var http = host.Client();
        // Act / Assert
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync("/api/sync/forces", null)).StatusCode);
        http.DefaultRequestHeaders.Add("X-Police-Sync", "1");
        http.DefaultRequestHeaders.Add("Origin", "https://untrusted.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync("/api/sync/forces", null)).StatusCode);
        http.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await http.PostAsync("/api/sync/crimes", new StringContent(new string('x', 4097)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/sync/crimes", new { latitude = 91, longitude = 0, month = "2024-01" })).StatusCode);
        Assert.Equal(0, host.Handler.Calls);
    }

    [Fact]
    public async Task Cancellation_BeforeSync_MakesNoChangesAndReleasesAdmission()
    {
        // Arrange
        await using var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        var gate = new OperationLease(); var client = new FakeClient();
        var service = new PoliceSyncService(db, client, gate, TimeProvider.System);
        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SyncAsync("forces", null, new(true)));
        // Assert
        Assert.Equal(0, client.Calls); Assert.Empty(await db.Records.ToListAsync()); using var lease = gate.Acquire();
    }

    [Fact]
    public async Task Crime_MissingThenPresentPersistentId_PromotesIdentityWithoutDuplicate()
    {
        // Arrange
        await using var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        var value = new CrimeDto { Id = 1, Month = "2024-01", Category = "burglary" };
        var client = new FakeClient { Crimes = [value] };
        var service = new PoliceSyncService(db, client, new(), TimeProvider.System);
        await service.SyncAsync("crimes", Query, default);
        var firstSeen = (await db.Records.AsNoTracking().SingleAsync()).FirstSeenUtc;
        // Act
        client.Crimes = [value with { PersistentId = "stable" }];
        var promoted = await service.SyncAsync("crimes", Query, default);
        client.Crimes = [value];
        await service.SyncAsync("crimes", Query, default);
        client.Crimes = [value with { Id = 2, PersistentId = "stable" }];
        await service.SyncAsync("crimes", Query, default);
        // Assert
        var row = await db.Records.SingleAsync();
        Assert.Equal("persistent:stable", row.Key); Assert.Equal("2", row.SourceId);
        Assert.Equal(firstSeen, row.FirstSeenUtc); Assert.Equal(1, promoted.Updated); Assert.Equal(0, promoted.Inserted);
    }

    [Fact]
    public async Task DatabaseConstraintFailure_RollsBackAllChangesAndAllowsRetry()
    {
        // Arrange
        await using var db = PostgresFixture.Context(await postgres.CreateDatabaseAsync());
        var client = new FakeClient { Forces = [new() { Id = "one", Name = "Original" }] };
        var service = new PoliceSyncService(db, client, new(), TimeProvider.System);
        await service.SyncAsync("forces", null, default);
        // Test-only constraint fault, not application table creation.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"PoliceRecords\" ADD CONSTRAINT test_reject_key CHECK (\"Key\" <> 'rejected')");
        client.Forces = [new() { Id = "one", Name = "Changed" }, new() { Id = "rejected", Name = "New" }];
        // Act
        await Assert.ThrowsAsync<DatabaseOperationException>(() => service.SyncAsync("forces", null, default));
        // Assert
        Assert.Contains("Original", (await db.Records.AsNoTracking().SingleAsync()).Data);
        client.Forces = [new() { Id = "one", Name = "Changed" }];
        Assert.Equal(1, (await service.SyncAsync("forces", null, default)).Updated);
    }

    [Fact]
    public async Task DatabaseCommandTimeout_ReturnsSafeTimeoutAndPreservesRow()
    {
        // Arrange
        var connection = await postgres.CreateDatabaseAsync();
        var settings = new NpgsqlConnectionStringBuilder(connection) { CommandTimeout = 1 };
        await using var db = PostgresFixture.Context(settings.ConnectionString);
        var client = new FakeClient { Forces = [new() { Id = "one", Name = "Original" }] };
        var service = new PoliceSyncService(db, client, new(), TimeProvider.System);
        await service.SyncAsync("forces", null, default);
        await using var blocker = new NpgsqlConnection(connection);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT * FROM \"PoliceRecords\" FOR UPDATE", blocker, transaction);
        await command.ExecuteNonQueryAsync();
        client.Forces = [new() { Id = "one", Name = "Changed" }];
        // Act
        await Assert.ThrowsAsync<DatabaseTimeoutException>(() => service.SyncAsync("forces", null, default));
        // Assert
        await transaction.RollbackAsync();
        Assert.Contains("Original", (await db.Records.AsNoTracking().SingleAsync()).Data);
    }

    [Theory]
    [InlineData("crimes", "[{\"id\":1,\"category\":\"burglary\",\"month\":\"2024-01\"}]")]
    [InlineData("stop-searches", "[{\"type\":\"Person search\",\"datetime\":\"2024-01-01T12:00:00Z\"}]")]
    public async Task Host_LocationDatasets_SyncAndReadThroughRealControllers(string dataset, string payload)
    {
        // Arrange
        await using var host = new IngestionHost((_, _, _) => Task.FromResult(IngestionHost.Response(payload)));
        host.Settings["ConnectionStrings:DefaultConnection"] = await postgres.CreateDatabaseAsync();
        using var http = host.Client(); http.DefaultRequestHeaders.Add("X-Police-Sync", "1");
        // Act
        var sync = await http.PostAsJsonAsync("/api/sync/" + dataset, Query);
        var page = await http.GetFromJsonAsync<PersistedPage>("/api/data/" + dataset + "?latitude=53.8&longitude=-1.5&month=2024-01");
        // Assert
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode); Assert.Single(page!.Items);
        Assert.Equal(1, host.Handler.Calls);
    }

    [Fact]
    public async Task Host_ConcurrentSyncAndDeadline_RejectsContenderThenReleasesGate()
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new IngestionHost(async (_, _, token) =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return IngestionHost.Response("[]");
        });
        host.Settings["ConnectionStrings:DefaultConnection"] = await postgres.CreateDatabaseAsync();
        using var http = host.Client(); http.DefaultRequestHeaders.Add("X-Police-Sync", "1");
        // Act
        var first = http.PostAsync("/api/sync/forces", null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var contender = await http.PostAsync("/api/sync/forces", null);
        var csvContender = await http.PostAsync("/api/ingestion/forces", null);
        host.Clock.Advance(TimeSpan.FromSeconds(121));
        var timedOut = await first;
        // Assert
        Assert.Equal(HttpStatusCode.Conflict, contender.StatusCode); Assert.Equal(HttpStatusCode.Conflict, csvContender.StatusCode);
        Assert.Equal(HttpStatusCode.GatewayTimeout, timedOut.StatusCode); Assert.Equal(1, host.Handler.Calls);
        using var lease = host.Services.GetRequiredService<OperationLease>().Acquire();
    }

    [Fact]
    public async Task Host_StaticAssets_ServesFrontendWithSecurityHeaders()
    {
        // Arrange
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Test Proj.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        await using var host = new IngestionHost { WebRoot = Path.Combine(root.FullName, "Test Proj", "wwwroot") };
        using var http = host.Client();
        // Act / Assert
        foreach (var path in new[] { "/", "/app.js", "/styles.css" })
        {
            var response = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("connect-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }
        var html = await http.GetStringAsync("/");
        Assert.Contains("aria-live=\"polite\"", html); Assert.Contains("id=\"sync\"", html);
    }

    private sealed class FakeClient : IPoliceApiClient
    {
        public IReadOnlyList<ForceDto> Forces { get; set; } = [];
        public IReadOnlyList<CrimeDto> Crimes { get; set; } = [];
        public IReadOnlyList<StopSearchDto> Stops { get; set; } = [];
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ForceDto>> GetForcesAsync(CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(Forces); }
        public Task<IReadOnlyList<CrimeDto>> GetCrimesAsync(LocationMonth request, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(Crimes); }
        public Task<IReadOnlyList<StopSearchDto>> GetStopSearchesAsync(LocationMonth request, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(Stops); }
    }
}
