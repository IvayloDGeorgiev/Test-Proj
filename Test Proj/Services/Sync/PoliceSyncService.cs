using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Test_Proj.Clients.PoliceApi;
using Test_Proj.Contracts.Requests;
using Test_Proj.Export;
using Test_Proj.Persistence;
using Test_Proj.Validation;

namespace Test_Proj.Services.Sync;

[System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)]
public sealed record SyncResult(bool Success, string Dataset, int Received, int Inserted, int Updated,
    int Unchanged, int Removed, DateTimeOffset CompletedAtUtc);
public sealed record PersistedItem(string Key, JsonElement Data, DateTimeOffset FirstSeenUtc,
    DateTimeOffset UpdatedAtUtc, DateTimeOffset LastSeenUtc);
[System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)]
public sealed record PersistedPage(string Dataset, int Offset, int Limit, int TotalCount, bool HasMore, IReadOnlyList<PersistedItem> Items);

public sealed class PoliceSyncService(PoliceDbContext db, IPoliceApiClient client, OperationLease admission, TimeProvider clock)
{
    public Task<PersistedPage> ReadAsync(string dataset, LocationMonthRequest? input, int offset, int limit, CancellationToken token) =>
        ReadAsync(dataset, input, offset, limit, null, token);

    public async Task<SyncResult> SyncAsync(string dataset, LocationMonthRequest? input, CancellationToken token)
    {
        var request = Validate(dataset, input);
        token.ThrowIfCancellationRequested();
        using var lease = admission.Acquire();
        var records = dataset switch
        {
            "forces" => RecordMapping.Forces(await client.GetForcesAsync(token)),
            "crimes" => RecordMapping.Crimes(await client.GetCrimesAsync(request!, token), request!.Month),
            _ => RecordMapping.Stops(await client.GetStopSearchesAsync(request!, token))
        };
        token.ThrowIfCancellationRequested();
        var scope = Scope(dataset, request);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            // Database-wide transaction lock also rejects writers in a second application process.
            var locked = await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(90420260916) AS \"Value\"").SingleAsync(token);
            if (!locked) throw new ExportException("operation_busy", 409);
            var keys = records.Select(x => x.Key).ToArray();
            var sourceIds = records.Where(x => x.SourceId != null).Select(x => x.SourceId!).ToArray();
            var existing = await db.Records.Where(x => x.Dataset == dataset && x.Scope == scope &&
                (dataset == "stop-searches" || keys.Contains(x.Key) || (x.SourceId != null && sourceIds.Contains(x.SourceId))))
                .Take(100001).ToDictionaryAsync(x => x.Key, token);
            if (existing.Count > 100000) throw new Test_Proj.Errors.PoliceApiException(Test_Proj.Errors.PoliceApiFailure.ResponseLimit);
            var bySource = existing.Values.Where(x => x.SourceId != null).ToDictionary(x => x.SourceId!, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int inserted = 0, updated = 0, unchanged = 0;
            var now = clock.GetUtcNow();
            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                seen.Add(record.Key);
                existing.TryGetValue(record.Key, out var entity);
                if (entity is null && record.SourceId is { } source) bySource.TryGetValue(source, out entity);
                if (entity is null)
                {
                    db.Records.Add(new() { Dataset = dataset, Scope = scope, Key = record.Key, SourceId = record.SourceId, Data = record.Data,
                        FirstSeenUtc = now, UpdatedAtUtc = now, LastSeenUtc = now });
                    inserted++;
                }
                else
                {
                    // Promote a fallback ID when a persistent ID becomes available; preserve first-seen metadata.
                    if (dataset == "crimes" && entity.Key.StartsWith("id:", StringComparison.Ordinal) && record.Key.StartsWith("persistent:", StringComparison.Ordinal))
                    {
                        db.Records.Remove(entity);
                        var replacement = new PoliceRecord { Dataset = dataset, Scope = scope, Key = record.Key,
                            SourceId = record.SourceId, Data = record.Data, FirstSeenUtc = entity.FirstSeenUtc,
                            UpdatedAtUtc = now < entity.LastSeenUtc ? entity.LastSeenUtc : now,
                            LastSeenUtc = now < entity.LastSeenUtc ? entity.LastSeenUtc : now };
                        db.Records.Add(replacement);
                        updated++;
                        continue;
                    }
                    // A backwards wall clock must not violate persisted time ordering.
                    var observed = now < entity.LastSeenUtc ? entity.LastSeenUtc : now;
                    if (entity.Data != record.Data) { entity.Data = record.Data; entity.UpdatedAtUtc = observed; updated++; }
                    else unchanged++;
                    entity.LastSeenUtc = observed;
                    entity.SourceId = record.SourceId;
                }
            }
            // Only stop/search is a query snapshot: no event IDs exist to match corrections reliably.
            var removed = dataset == "stop-searches" ? existing.Values.Where(x => !seen.Contains(x.Key)).ToArray() : [];
            db.Records.RemoveRange(removed);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            // Commit is the success boundary. Never turn a committed sync into a deadline failure.
            return new(true, dataset, records.Count, inserted, updated, unchanged, removed.Length, clock.GetUtcNow());
        }
        catch (OperationCanceledException) { throw; }
        catch (ExportException) { throw; }
        catch (Test_Proj.Errors.PoliceApiException) { throw; }
        catch (Exception error) { throw Classify(error); }
        finally { db.ChangeTracker.Clear(); }
    }

    public async Task<PersistedPage> ReadAsync(string dataset, LocationMonthRequest? input, int offset, int limit, string? search, CancellationToken token)
    {
        var request = Validate(dataset, input);
        if (offset is < 0 or > 1000000 || limit is < 1 or > 200) throw Invalid();
        var scope = Scope(dataset, request);
        try
        {
            search = search?.Trim();
            if (search is { Length: > 100 }) throw Invalid();
            var query = db.Records.AsNoTracking().Where(x => x.Dataset == dataset && x.Scope == scope);
            if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Key.Contains(search) || x.Data.Contains(search));
            var total = await query.CountAsync(token);
            var rows = await query.OrderBy(x => x.Key).Skip(offset).Take(limit + 1).ToListAsync(token);
            return new(dataset, offset, limit, total, rows.Count > limit, rows.Take(limit).Select(x =>
                new PersistedItem(x.Key, JsonSerializer.Deserialize<JsonElement>(x.Data), x.FirstSeenUtc, x.UpdatedAtUtc, x.LastSeenUtc)).ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw Classify(error); }
    }

    private static Exception Classify(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is TimeoutException) return new DatabaseTimeoutException();
        return new DatabaseOperationException();
    }

    private static string Scope(string dataset, LocationMonth? request) => dataset switch
    { "forces" => "all", "crimes" => request!.Month, _ => RecordMapping.Scope(request!) };
    private static LocationMonth? Validate(string dataset, LocationMonthRequest? input)
    {
        if (dataset is not ("forces" or "crimes" or "stop-searches")) throw Invalid();
        return dataset == "forces" ? null : LocationMonth.Create(input?.Latitude, input?.Longitude, input?.Month);
    }
    private static RequestValidationException Invalid() => new(new Dictionary<string, string[]> { ["request"] = ["Supply a valid dataset and page."] });
}
